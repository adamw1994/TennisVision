internal sealed class DetectorSettings
{
    public string FFmpegPath { get; set; } = "ffmpeg";
    public double WindowMilliseconds { get; set; } = 20;
    public double HopMilliseconds { get; set; } = 10;
    public double BackgroundSeconds { get; set; } = 0.5;
    public double BackgroundUpdateLimit { get; set; } = 3;
    public double NoiseFloorRms { get; set; } = 0.002;
    public double MinimumRms { get; set; } = 0.006;
    public double ThresholdMultiplier { get; set; } = 2;
    public double MinimumRiseRatio { get; set; } = 1.15;
    public double MinimumPeakDistanceSeconds { get; set; } = 0.12;
    // Fraction of adjacent samples with different signs; 0 disables this filter.
    public double MinimumZeroCrossingRate { get; set; } = 0;
    public double MaximumGapBetweenHitsSeconds { get; set; } = 3.0;
    public PlayerDetectionSettings PlayerDetection { get; set; } = new();
    public RallyAnalysisSettings RallyAnalysis { get; set; } = new();
    public VideoExportSettings VideoExport { get; set; } = new();

    public void Validate()
    {
        if (!double.IsFinite(MaximumGapBetweenHitsSeconds) || MaximumGapBetweenHitsSeconds < 0)
            throw new InvalidDataException("MaximumGapBetweenHitsSeconds must be finite and nonnegative.");

        double[] numbers = [WindowMilliseconds, HopMilliseconds, BackgroundSeconds,
            BackgroundUpdateLimit, NoiseFloorRms, MinimumRms, ThresholdMultiplier,
            MinimumRiseRatio, MinimumPeakDistanceSeconds, MinimumZeroCrossingRate];

        if (string.IsNullOrWhiteSpace(FFmpegPath) || numbers.Any(n => !double.IsFinite(n)) ||
            WindowMilliseconds < 1 || WindowMilliseconds > 1000 ||
            HopMilliseconds < 1 || HopMilliseconds > WindowMilliseconds ||
            BackgroundSeconds <= 0 || BackgroundUpdateLimit < 1 ||
            NoiseFloorRms <= 0 || NoiseFloorRms > 1 || MinimumRms < 0 || MinimumRms > 1 ||
            ThresholdMultiplier < 1 || MinimumRiseRatio < 1 || MinimumPeakDistanceSeconds < 0 ||
            MinimumZeroCrossingRate < 0 || MinimumZeroCrossingRate > 1)
        {
            throw new InvalidDataException("Invalid detection settings. Windows must be 1–1000 ms, " +
                "hop must be 1 ms to window size, background duration must be positive, " +
                "ratios must be at least 1, RMS values must be between 0 and 1 " +
                "(noise floor must be positive), peak distance must be nonnegative, " +
                "and minimum zero-crossing rate must be between 0 and 1.");
        }
    }
}
