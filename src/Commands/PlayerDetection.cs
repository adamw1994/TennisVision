using System.Globalization;
using System.Text;
using System.Text.Json;

internal sealed class PlayerDetectionSettings
{
    public string ModelPath { get; set; } = "models/yolox_l.onnx";
    public double FrameIntervalSeconds { get; set; } = 0.5;
    public float ConfidenceThreshold { get; set; } = 0.25f;
    public float NmsIouThreshold { get; set; } = 0.45f;
    public string OutputDirectory { get; set; } = "output/player-detection";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath) || string.IsNullOrWhiteSpace(OutputDirectory) ||
            !double.IsFinite(FrameIntervalSeconds) || FrameIntervalSeconds < 0.001 ||
            !float.IsFinite(ConfidenceThreshold) || ConfidenceThreshold <= 0 || ConfidenceThreshold > 1 ||
            !float.IsFinite(NmsIouThreshold) || NmsIouThreshold < 0 || NmsIouThreshold > 1)
            throw new InvalidDataException("Invalid PlayerDetection settings: provide model/output paths, " +
                "a frame interval >= 0.001 seconds, confidence in (0,1], and NMS IoU in [0,1].");
    }
}

internal static class PlayerDetection
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is not (3 or 4) || !int.TryParse(args[2], out int rowNumber) || rowNumber < 1)
            throw new ArgumentException("Usage: dotnet run -- detect-players \"video.mp4\" \"rallies.csv\" <row-number> [model.onnx]. " +
                "Row 1 is the first data row, excluding the header.");
        string videoPath = Path.GetFullPath(args[0]);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video file not found.", videoPath);
        var settings = JsonSerializer.Deserialize<DetectorSettings>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
            ?? throw new InvalidDataException("Missing settings object.");
        var options = settings.PlayerDetection;
        options.Validate();
        var (start, end) = ReadRally(args[1], rowNumber);
        string modelPath = Path.GetFullPath(args.Length == 4 ? args[3] : options.ModelPath);
        using var detector = new PersonDetector(modelPath);
        var extractor = new VideoFrameExtractor(settings.FFmpegPath);
        string directory = Path.GetFullPath(Path.Combine(options.OutputDirectory,
            $"{Path.GetFileNameWithoutExtension(videoPath)}-{Path.GetFileNameWithoutExtension(modelPath)}-rally-{rowNumber:D4}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}"));
        Directory.CreateDirectory(directory);
        using var csv = new StreamWriter(Path.Combine(directory, "detections.csv"), false, new UTF8Encoding(false));
        csv.WriteLine("TimestampSeconds,DetectionCount,BoundingBoxes,ConfidenceScores,FrameFile");
        int frameCount = 0;
        double previousTimestamp = double.NegativeInfinity;
        // Even a single-impact rally requests its one frame. Longer rallies sample start + n*interval.
        for (long index = 0; ; index++)
        {
            double requestedTime = start + index * options.FrameIntervalSeconds;
            if (requestedTime > end + 1e-9)
                break;
            var extracted = await extractor.ExtractAsync(videoPath, requestedTime);
            if (extracted is null)
                throw new InvalidDataException($"No video frame at {requestedTime:F6}s. " +
                    "Check that rallies.csv belongs to this video; any output is incomplete.");
            using var image = extracted.Value.Image;
            double timestamp = extracted.Value.TimestampSeconds;
            // FFmpeg chooses the first frame at/after the requested time. Do not repeat it.
            if (timestamp <= previousTimestamp)
                continue;
            // A single-impact group has no duration: keep its nearest following frame.
            if (start != end && timestamp > end + 1e-6)
                break;
            previousTimestamp = timestamp;
            var people = detector.Detect(image, options.ConfidenceThreshold, options.NmsIouThreshold);
            string filename = FormattableString.Invariant($"frame-{frameCount + 1:D5}-{timestamp:F6}s.png");
            PersonDetector.SaveAnnotated(image, people, Path.Combine(directory, filename));
            string boxes = JsonSerializer.Serialize(people.Select(b => new[] { b.X1, b.Y1, b.X2, b.Y2 }));
            string scores = JsonSerializer.Serialize(people.Select(b => b.Confidence));
            csv.WriteLine(FormattableString.Invariant(
                $"{timestamp:F6},{people.Count},{Quote(boxes)},{Quote(scores)},{Quote(filename)}"));
            frameCount++;
            Console.WriteLine(FormattableString.Invariant($"{timestamp:F6}s: {people.Count} person detections"));
        }
        Console.WriteLine($"Saved {frameCount} annotated frames and detections.csv to: {directory}");
        return 0;
    }

    private static (double Start, double End) ReadRally(string path, int rowNumber)
    {
        using var lines = File.ReadLines(path).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.TrimStart('\uFEFF') != "StartSeconds,EndSeconds,HitCount")
            throw new InvalidDataException("Expected the application's rallies.csv header.");
        for (int row = 1; row <= rowNumber; row++)
            if (!lines.MoveNext())
                throw new ArgumentException($"rallies.csv has no data row {rowNumber}.");
        string[] cells = lines.Current.Split(',');
        if (cells.Length != 3 ||
            !double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
            !double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
            throw new InvalidDataException($"Invalid rally timestamps in row {rowNumber}.");
        return (start, end);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
