using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal sealed class VideoExportSettings
{
    public string FFprobePath { get; set; } = "ffprobe";
    public string OutputDirectory { get; set; } = "output/runs";
    public double PreRollSeconds { get; set; } = 2;
    public double PostRollSeconds { get; set; } = 3;
    public double MergeGapSeconds { get; set; } = 1;

    public void Validate()
    {
        double[] durations = [PreRollSeconds, PostRollSeconds, MergeGapSeconds];
        if (string.IsNullOrWhiteSpace(FFprobePath) || string.IsNullOrWhiteSpace(OutputDirectory) ||
            durations.Any(v => !double.IsFinite(v) || v < 0))
            throw new InvalidDataException("VideoExport requires an FFprobePath, an output directory, and finite, nonnegative durations.");
    }
}

internal sealed class VideoExporter(string ffmpegPath, VideoExportSettings settings)
{
    internal readonly record struct VideoInfo(
        double DurationSeconds, double StartSeconds, double AudioOffsetSeconds, double FrameDurationSeconds);

    public async Task<VideoInfo> ProbeAsync(string path)
    {
        string json = await RunAsync(settings.FFprobePath,
            ["-v", "error", "-show_entries", "format=duration,start_time:stream=codec_type,start_time,avg_frame_rate",
                "-of", "json", path]);
        using var document = JsonDocument.Parse(json);
        var format = document.RootElement.GetProperty("format");
        double duration = ReadNumber(format, "duration", double.NaN);
        double start = ReadNumber(format, "start_time", 0);
        if (!double.IsFinite(duration) || duration <= 0 || !double.IsFinite(start))
            throw new InvalidDataException("FFprobe did not report a finite positive video duration.");
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        if (video.ValueKind == JsonValueKind.Undefined || audio.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("The source must contain both video and audio.");
        double audioOffset = ReadNumber(audio, "start_time", start) - start;
        double frameDuration = 1.0 / 30;
        if (video.TryGetProperty("avg_frame_rate", out var rate))
        {
            string[] fraction = (rate.GetString() ?? "").Split('/');
            if (fraction.Length == 2 &&
                double.TryParse(fraction[0], CultureInfo.InvariantCulture, out double numerator) &&
                double.TryParse(fraction[1], CultureInfo.InvariantCulture, out double denominator) &&
                numerator > 0 && denominator > 0)
                frameDuration = denominator / numerator;
        }
        return new VideoInfo(duration, start, audioOffset, frameDuration);
    }

    public List<(double Start, double End)> BuildIntervals(
        IEnumerable<(double StartSeconds, double EndSeconds, int HitCount)> rallies, VideoInfo video)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var rally in rallies.OrderBy(r => r.StartSeconds))
        {
            // Impact times start at the decoded audio's beginning. Map them to the source timeline.
            double start = Math.Clamp(rally.StartSeconds + video.AudioOffsetSeconds - settings.PreRollSeconds,
                0, video.DurationSeconds);
            double end = Math.Clamp(rally.EndSeconds + video.AudioOffsetSeconds + settings.PostRollSeconds,
                0, video.DurationSeconds);
            // Preserve a single-impact event even when both padding settings are zero.
            if (end <= start)
            {
                end = Math.Min(video.DurationSeconds, start + video.FrameDurationSeconds);
                start = Math.Max(0, end - video.FrameDurationSeconds);
            }
            if (merged.Count > 0 && start - merged[^1].End <= settings.MergeGapSeconds)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }
        return merged;
    }

    public async Task<double> ExportAsync(string source, string destination,
        IReadOnlyList<(double Start, double End)> intervals, VideoInfo video)
    {
        // Stage beside the destination, so publishing the completed MP4 is a same-volume move.
        string temporaryDirectory = Path.Combine(Path.GetDirectoryName(destination)!,
            ".tennisvision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        string listPath = Path.Combine(temporaryDirectory, "segments.ffconcat");
        string stagedOutput = Path.Combine(temporaryDirectory, "highlights.mp4");
        try
        {
            using (var list = new StreamWriter(listPath, false, new UTF8Encoding(false)))
            {
                list.WriteLine("ffconcat version 1.0");
                string quotedSource = source.Replace('\\', '/').Replace("'", "'\\''");
                foreach (var interval in intervals)
                {
                    list.WriteLine($"file '{quotedSource}'");
                    list.WriteLine(FormattableString.Invariant($"inpoint {interval.Start + video.StartSeconds:F9}"));
                    list.WriteLine(FormattableString.Invariant($"outpoint {interval.End + video.StartSeconds:F9}"));
                    list.WriteLine(FormattableString.Invariant($"duration {interval.End - interval.Start:F9}"));
                }
            }

            double selectedDuration = intervals.Sum(i => i.End - i.Start);
            Console.WriteLine(FormattableString.Invariant($"Exporting {intervals.Count} intervals, approximately {selectedDuration:F3} s."));
            // concatdec_select removes keyframe preroll/overrun after decoding. Both streams
            // keep the concat demuxer's shared timestamps; they are not reset independently.
            // Resampling fills small audio boundary gaps without accumulating A/V drift.
            await RunAsync(ffmpegPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-copyts",
                    "-f", "concat", "-safe", "0", "-segment_time_metadata", "1", "-i", listPath,
                    "-map", "0:v:0", "-map", "0:a:0", "-vf", "select=concatdec_select",
                    "-af", "aselect=concatdec_select,aresample=async=1:first_pts=0",
                    "-fps_mode", "vfr", "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-ar", "48000",
                    "-map_metadata", "-1", "-map_chapters", "-1", "-movflags", "+faststart",
                    "-stats_period", "5", "-progress", "pipe:1", stagedOutput], selectedDuration);
            double actualDuration = (await ProbeAsync(stagedOutput)).DurationSeconds;
            File.Move(stagedOutput, destination); // Never replace an existing source or output.
            return actualDuration;
        }
        finally
        {
            // Delete only the exact files created by this export, never a recursive path.
            if (File.Exists(stagedOutput)) File.Delete(stagedOutput);
            if (File.Exists(listPath)) File.Delete(listPath);
            Directory.Delete(temporaryDirectory);
        }
    }

    private static double ReadNumber(JsonElement element, string name, double fallback) =>
        element.TryGetProperty(name, out var value) &&
        double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out double result) ? result : fallback;

    private static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        double? expectedDuration = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> errorsTask = process.StandardError.ReadToEndAsync();
        var output = new StringBuilder();
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!expectedDuration.HasValue) output.AppendLine(line);
                else if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_us=".Length), out long microseconds))
                    Console.WriteLine(FormattableString.Invariant(
                        $"  Export progress: {microseconds / 1_000_000.0:F1} / {expectedDuration.Value:F1} s"));
            }
            await process.WaitForExitAsync();
            string errors = await errorsTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{executable} failed (exit {process.ExitCode}): {errors.Trim()}");
            return output.ToString();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await errorsTask;
            throw;
        }
    }
}
