using System.Globalization;
using System.Text;
using System.Text.Json;

internal sealed class RallyAnalysisSettings
{
    public double ShortRallyDurationSeconds { get; set; } = 1;
    public double ContextBeforeSeconds { get; set; } = 1;
    public double ContextAfterSeconds { get; set; } = 1;
    public string OutputDirectory { get; set; } = "output/rally-analysis";

    public void Validate()
    {
        double[] values = [ShortRallyDurationSeconds, ContextBeforeSeconds, ContextAfterSeconds];
        if (values.Any(v => !double.IsFinite(v) || v < 0) || string.IsNullOrWhiteSpace(OutputDirectory))
            throw new InvalidDataException("RallyAnalysis requires finite, nonnegative durations and an output directory.");
    }
}

internal static class RallyAnalysis
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("Usage: dotnet run -- analyze-rallies \"video.mp4\" \"rallies.csv\"");
        string videoPath = Path.GetFullPath(args[0]);
        string ralliesPath = Path.GetFullPath(args[1]);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found.", videoPath);
        if (!File.Exists(ralliesPath))
            throw new FileNotFoundException("Rallies CSV not found.", ralliesPath);
        var settings = JsonSerializer.Deserialize<DetectorSettings>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
            ?? throw new InvalidDataException("Missing settings object.");
        var options = settings.RallyAnalysis;
        options.Validate();
        var playerOptions = settings.PlayerDetection;
        playerOptions.Validate();

        // One model and inference session for every frame in every rally.
        using var detector = new PersonDetector(Path.GetFullPath(playerOptions.ModelPath));
        var extractor = new VideoFrameExtractor(settings.FFmpegPath);
        string directory = Path.GetFullPath(Path.Combine(options.OutputDirectory,
            $"{Path.GetFileNameWithoutExtension(videoPath)}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}"));
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "rally-analysis.csv");
        using var report = new StreamWriter(reportPath, false, new UTF8Encoding(false));
        report.WriteLine("RallyIndex,StartSeconds,EndSeconds,HitCount,SampledFrameCount," +
            "ZeroPeopleFrameCount,OnePersonFrameCount,TwoOrMorePeopleFrameCount," +
            "AveragePeoplePerFrame,MaxPeopleInFrame,FirstSampleTimestampSeconds,LastSampleTimestampSeconds");

        int rallyIndex = 0;
        foreach (var rally in ReadRallies(ralliesPath))
        {
            rallyIndex++;
            bool useContext = rally.HitCount == 1 || rally.End - rally.Start <= options.ShortRallyDurationSeconds;
            double sampleStart = useContext ? Math.Max(0, rally.Start - options.ContextBeforeSeconds) : rally.Start;
            double sampleEnd = useContext ? rally.End + options.ContextAfterSeconds : rally.End;
            if (!double.IsFinite(sampleEnd))
                throw new InvalidDataException($"Sampling range overflows for rally {rallyIndex}.");
            Console.WriteLine(FormattableString.Invariant(
                $"Rally {rallyIndex}: {rally.Start:F3}-{rally.End:F3}s, {rally.HitCount} audio impacts; sampling {sampleStart:F3}-{sampleEnd:F3}s"));

            int frameCount = 0, zeroCount = 0, oneCount = 0, twoOrMoreCount = 0, maxPeople = 0;
            long totalPeople = 0;
            double? firstTimestamp = null, lastTimestamp = null;
            for (long index = 0; ; index++)
            {
                double requestedTime = sampleStart + index * playerOptions.FrameIntervalSeconds;
                if (requestedTime > sampleEnd + 1e-9)
                    break;
                var extracted = await extractor.ExtractAsync(videoPath, requestedTime);
                if (extracted is null)
                {
                    // Context can extend beyond the video. Count only frames actually decoded.
                    Console.WriteLine($"  No frame at {requestedTime.ToString("F6", CultureInfo.InvariantCulture)}s; " +
                        "reached the end of available video for this sampling window.");
                    break;
                }
                using var image = extracted.Value.Image;
                double timestamp = extracted.Value.TimestampSeconds;
                if (lastTimestamp.HasValue && timestamp <= lastTimestamp.Value)
                    continue;
                // A zero-duration window still gets its first following frame.
                if (sampleStart != sampleEnd && timestamp > sampleEnd + 1e-6)
                    break;
                int people = detector.Detect(image, playerOptions.ConfidenceThreshold,
                    playerOptions.NmsIouThreshold).Count;
                firstTimestamp ??= timestamp;
                lastTimestamp = timestamp;
                frameCount++;
                if (people == 0) zeroCount++;
                else if (people == 1) oneCount++;
                else twoOrMoreCount++;
                totalPeople += people;
                maxPeople = Math.Max(maxPeople, people);
                Console.WriteLine(FormattableString.Invariant(
                    $"  Frame {frameCount} at {timestamp:F6}s: {people} person detections"));
            }

            string average = frameCount > 0
                ? ((double)totalPeople / frameCount).ToString("F6", CultureInfo.InvariantCulture) : "";
            string first = firstTimestamp?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
            string last = lastTimestamp?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
            report.WriteLine(FormattableString.Invariant(
                $"{rallyIndex},{rally.Start:F6},{rally.End:F6},{rally.HitCount},{frameCount},{zeroCount},{oneCount},{twoOrMoreCount},{average},{maxPeople},{first},{last}"));
            report.Flush();
            Console.WriteLine($"Rally {rallyIndex} complete: {frameCount} sampled frames.");
        }
        Console.WriteLine($"Analyzed {rallyIndex} candidate rallies. Report: {reportPath}");
        return 0;
    }

    private static IEnumerable<(double Start, double End, int HitCount)> ReadRallies(string path)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.TrimStart('\uFEFF') != "StartSeconds,EndSeconds,HitCount")
            throw new InvalidDataException("Expected the application's rallies.csv header.");
        int row = 0;
        while (lines.MoveNext())
        {
            row++;
            string[] cells = lines.Current.Split(',');
            if (cells.Length != 3 ||
                !double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                !double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double end) ||
                !int.TryParse(cells[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hitCount) ||
                !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start || hitCount < 1)
                throw new InvalidDataException($"Invalid rally data in row {row}.");
            yield return (start, end, hitCount);
        }
    }
}
