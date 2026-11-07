using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const int SampleRate = 16_000;

    // Measurements belong to the selected window, before updating the background.
    private readonly record struct Detection(
        double Time, double Rms, double Strength, double BackgroundRms,
        double PreviousRms, double RiseRatio, double PeakAmplitude,
        double CrestFactor, double ZeroCrossingRate, double RmsThreshold);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "detect-players")
                return await PlayerDetection.RunAsync(args[1..]);
            if (args.Length > 0 && args[0] == "analyze-rallies")
                return await RallyAnalysis.RunAsync(args[1..]);

            if (args.Length != 1)
            {
                Console.Error.WriteLine("Usage: dotnet run -- \"path/to/match.mp4\"");
                Console.Error.WriteLine("   or: dotnet run -- detect-players \"video.mp4\" \"rallies.csv\" <row-number> [model.onnx]");
                Console.Error.WriteLine("   or: dotnet run -- analyze-rallies \"video.mp4\" \"rallies.csv\"");
                return 1;
            }

            var totalTimer = Stopwatch.StartNew();
            string inputPath = Path.GetFullPath(args[0]);
            if (!File.Exists(inputPath))
                throw new FileNotFoundException("Video file not found.", inputPath);

            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var settings = JsonSerializer.Deserialize<DetectorSettings>(File.ReadAllText(settingsPath))
                ?? throw new InvalidDataException("appsettings.json must contain a settings object.");
            settings.Validate();
            settings.VideoExport.Validate();
            var exporter = new VideoExporter(settings.FFmpegPath, settings.VideoExport);
            var videoInfo = await exporter.ProbeAsync(inputPath);
            Console.WriteLine(FormattableString.Invariant($"Original video duration: {videoInfo.DurationSeconds:F3} s"));

            string videoName = Path.GetFileNameWithoutExtension(inputPath);
            string runDirectory = Path.GetFullPath(Path.Combine(settings.VideoExport.OutputDirectory,
                $"{videoName}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(runDirectory);
            Console.WriteLine($"Results directory: {runDirectory}");
            string outputPath = Path.Combine(runDirectory, videoName + "-highlights.mp4");
            string csvPath = Path.Combine(runDirectory, "impacts.csv");
            var impactTimestamps = new List<double>();
            var audioTimer = Stopwatch.StartNew();
            var startInfo = new ProcessStartInfo(settings.FFmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // WAV is sent through a pipe, so no full recording or temporary WAV is stored.
            string[] ffmpegArgs = ["-hide_banner", "-loglevel", "error", "-nostdin",
                "-i", inputPath, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000",
                "-c:a", "pcm_s16le", "-f", "wav", "pipe:1"];
            foreach (string argument in ffmpegArgs)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start FFmpeg.");
            // Drain stderr concurrently so FFmpeg cannot block on a full error pipe.
            Task<string> errorsTask = process.StandardError.ReadToEndAsync();
            int count;
            try
            {
                using var reader = new BinaryReader(new BufferedStream(process.StandardOutput.BaseStream, 65536));
                long dataBytes = ReadWavHeader(reader);
                using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
                csv.WriteLine("TimestampSeconds,RmsEnergy,RelativePeakStrength,BackgroundRms," +
                    "PreviousWindowRms,RiseRatio,PeakAmplitude,CrestFactor,ZeroCrossingRate,EffectiveRmsThreshold");
                Console.WriteLine("Potential racket impacts (other transient sounds may also be detected):");
                count = Detect(reader, dataBytes, settings, csv, impactTimestamps);
                await process.WaitForExitAsync();
            }
            catch (Exception exception)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                string errors = await errorsTask;
                throw new InvalidOperationException(
                    $"Audio processing failed: {exception.Message} {errors.Trim()}", exception);
            }

            string ffmpegErrors = await errorsTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg failed; any CSV is incomplete. {ffmpegErrors.Trim()}");
            audioTimer.Stop();

            Console.WriteLine($"Detected {count} potential impacts. CSV: {csvPath}");

            string ralliesPath = Path.Combine(runDirectory, "rallies.csv");
            var rallyDetector = new RallyDetector(settings.MaximumGapBetweenHitsSeconds);
            var rallies = rallyDetector.Detect(impactTimestamps).ToList();
            using (var ralliesCsv = new StreamWriter(ralliesPath, false, new UTF8Encoding(false)))
            {
                ralliesCsv.WriteLine("StartSeconds,EndSeconds,HitCount");
                foreach (var rally in rallies)
                    ralliesCsv.WriteLine(FormattableString.Invariant(
                        $"{rally.StartSeconds:F6},{rally.EndSeconds:F6},{rally.HitCount}"));
            }
            Console.WriteLine($"Grouped into {rallies.Count} candidate actions. CSV: {ralliesPath}");

            var intervals = exporter.BuildIntervals(rallies, videoInfo);
            Console.WriteLine($"Merged video intervals: {intervals.Count}");
            var exportTimer = Stopwatch.StartNew();
            double outputDuration = 0;
            if (intervals.Count == 0)
                Console.WriteLine("No exportable candidate actions detected; no highlights MP4 was created.");
            else
            {
                outputDuration = await exporter.ExportAsync(inputPath, outputPath, intervals, videoInfo);
                Console.WriteLine($"Highlights: {outputPath}");
            }
            exportTimer.Stop();
            totalTimer.Stop();
            Console.WriteLine(FormattableString.Invariant($"Output video duration: {outputDuration:F3} s"));
            Console.WriteLine(FormattableString.Invariant($"Original video retained: {100 * outputDuration / videoInfo.DurationSeconds:F2}%"));
            Console.WriteLine(FormattableString.Invariant($"Audio analysis time: {audioTimer.Elapsed.TotalSeconds:F2} s"));
            Console.WriteLine(FormattableString.Invariant($"Video export time: {exportTimer.Elapsed.TotalSeconds:F2} s"));
            Console.WriteLine(FormattableString.Invariant($"Total processing time: {totalTimer.Elapsed.TotalSeconds:F2} s"));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int Detect(BinaryReader reader, long dataBytes, DetectorSettings settings, StreamWriter csv,
        List<double> impactTimestamps)
    {
        int windowSize = (int)Math.Round(SampleRate * settings.WindowMilliseconds / 1000);
        int hopSize = (int)Math.Round(SampleRate * settings.HopMilliseconds / 1000);
        var samples = new double[windowSize];
        int filled = 0;
        long windowStart = 0;
        long bytesRemaining = dataBytes;
        double background = settings.NoiseFloorRms;
        double previousRms = settings.NoiseFloorRms;
        double smoothing = 1 - Math.Exp(-(double)hopSize / SampleRate / settings.BackgroundSeconds);
        Detection? pending = null;
        double lastEmittedTime = double.NegativeInfinity;
        int count = 0;

        void EmitPending()
        {
            if (pending is not { } peak)
                return;
            Console.WriteLine(peak.Time.ToString("F3", CultureInfo.InvariantCulture) + " s");
            csv.WriteLine(FormattableString.Invariant($"{peak.Time:F6},{peak.Rms:F8},{peak.Strength:F4},{peak.BackgroundRms:F8},{peak.PreviousRms:F8},{peak.RiseRatio:F4},{peak.PeakAmplitude:F8},{peak.CrestFactor:F4},{peak.ZeroCrossingRate:F6},{peak.RmsThreshold:F8}"));
            impactTimestamps.Add(peak.Time);
            lastEmittedTime = peak.Time;
            count++;
            pending = null;
        }

        while (true)
        {
            bool readNewSamples = false;
            while (filled < windowSize && bytesRemaining >= 2)
            {
                int low = reader.BaseStream.ReadByte();
                if (low < 0)
                {
                    if (dataBytes != long.MaxValue)
                        throw new EndOfStreamException("Truncated WAV data.");
                    break;
                }
                int high = reader.BaseStream.ReadByte();
                if (high < 0)
                    throw new EndOfStreamException("Incomplete PCM sample.");
                samples[filled++] = (short)(low | (high << 8)) / 32768.0;
                bytesRemaining -= 2;
                readNewSamples = true;
            }

            if (!readNewSamples)
                break;

            double sumSquares = 0;
            double peakAmplitude = 0;
            int zeroCrossings = 0;
            for (int i = 0; i < filled; i++)
            {
                sumSquares += samples[i] * samples[i];
                peakAmplitude = Math.Max(peakAmplitude, Math.Abs(samples[i]));
                if (i > 0 && (samples[i] >= 0) != (samples[i - 1] >= 0))
                    zeroCrossings++;
            }
            double rms = Math.Sqrt(sumSquares / filled);
            double zeroCrossingRate = filled > 1 ? (double)zeroCrossings / (filled - 1) : 0;
            // Times are window centers relative to the beginning of the decoded audio.
            double time = (windowStart + filled / 2.0) / SampleRate;
            double strength = rms / Math.Max(background, settings.NoiseFloorRms);
            double rise = rms / Math.Max(previousRms, settings.NoiseFloorRms);

            if (pending is { } oldPeak && time - oldPeak.Time >= settings.MinimumPeakDistanceSeconds)
                EmitPending();

            bool candidate = rms >= settings.MinimumRms && strength >= settings.ThresholdMultiplier &&
                rise >= settings.MinimumRiseRatio && zeroCrossingRate >= settings.MinimumZeroCrossingRate;
            if (candidate && time - lastEmittedTime >= settings.MinimumPeakDistanceSeconds)
            {
                if (pending is null || rms > pending.Value.Rms)
                {
                    double effectiveBackground = Math.Max(background, settings.NoiseFloorRms);
                    pending = new Detection(time, rms, strength, effectiveBackground, previousRms, rise,
                        peakAmplitude, rms > 0 ? peakAmplitude / rms : 0,
                        zeroCrossingRate,
                        Math.Max(settings.MinimumRms, effectiveBackground * settings.ThresholdMultiplier));
                }
            }

            // Limit each update so a loud transient does not immediately raise the background.
            double limitedRms = Math.Min(rms, Math.Max(background, settings.NoiseFloorRms) * settings.BackgroundUpdateLimit);
            background += smoothing * (limitedRms - background);
            previousRms = rms;

            if (filled < windowSize)
                break;
            Array.Copy(samples, hopSize, samples, 0, windowSize - hopSize);
            filled = windowSize - hopSize;
            windowStart += hopSize;
        }

        EmitPending();
        return count;
    }

    // FFmpeg writes a RIFF WAV with an unknown data length (0xffffffff) to stdout.
    // Walk chunks rather than assuming every WAV has a fixed 44-byte header.
    private static long ReadWavHeader(BinaryReader reader)
    {
        string ReadId() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (ReadId() != "RIFF")
            throw new InvalidDataException("Expected a RIFF WAV stream from FFmpeg.");
        reader.ReadUInt32();
        if (ReadId() != "WAVE")
            throw new InvalidDataException("Expected a WAVE stream.");

        bool validFormat = false;
        while (true)
        {
            string id = ReadId();
            uint size = reader.ReadUInt32();
            if (id == "data")
            {
                if (!validFormat)
                    throw new InvalidDataException("Missing PCM format chunk.");
                return size == uint.MaxValue ? long.MaxValue : size;
            }

            long remaining = size;
            if (id == "fmt ")
            {
                if (size < 16)
                    throw new InvalidDataException("Invalid WAV format chunk.");
                ushort format = reader.ReadUInt16();
                ushort channels = reader.ReadUInt16();
                uint rate = reader.ReadUInt32();
                reader.ReadUInt32(); // Byte rate.
                ushort blockAlign = reader.ReadUInt16();
                ushort bits = reader.ReadUInt16();
                if (format != 1 || channels != 1 || rate != SampleRate || blockAlign != 2 || bits != 16)
                    throw new InvalidDataException("Expected mono, 16 kHz, 16-bit PCM audio.");
                validFormat = true;
                remaining -= 16;
            }

            remaining += size % 2; // RIFF chunks are padded to an even byte count.
            while (remaining > 0)
            {
                byte[] skipped = reader.ReadBytes((int)Math.Min(remaining, 4096));
                if (skipped.Length == 0)
                    throw new EndOfStreamException("Truncated WAV header.");
                remaining -= skipped.Length;
            }
        }
    }
}
