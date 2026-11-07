internal sealed class RallyDetector
{
    private readonly double maximumGapBetweenHitsSeconds;

    public RallyDetector(double maximumGapBetweenHitsSeconds = 3.0)
    {
        if (!double.IsFinite(maximumGapBetweenHitsSeconds) || maximumGapBetweenHitsSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumGapBetweenHitsSeconds));

        this.maximumGapBetweenHitsSeconds = maximumGapBetweenHitsSeconds;
    }

    // The audio detector already emits timestamps in chronological order.
    public IEnumerable<(double StartSeconds, double EndSeconds, int HitCount)> Detect(
        IEnumerable<double> impactTimestamps)
    {
        double start = 0;
        double end = 0;
        int hitCount = 0;

        foreach (double timestamp in impactTimestamps)
        {
            if (hitCount > 0 && timestamp - end > maximumGapBetweenHitsSeconds)
            {
                yield return (start, end, hitCount);
                hitCount = 0;
            }

            if (hitCount == 0)
                start = timestamp;

            end = timestamp;
            hitCount++;
        }

        if (hitCount > 0)
            yield return (start, end, hitCount);
    }
}
