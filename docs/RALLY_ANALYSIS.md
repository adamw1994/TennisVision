# Collecting rally visibility data

From the project directory:

Replace `RUN_FOLDER` with the main pipeline's result directory for this video,
printed as `Results directory`. Each run has its own `rallies.csv`.

```powershell
dotnet run -- analyze-rallies "C:\TennisSamples\Wojtek1set-2min2.mp4" "output\runs\RUN_FOLDER\rallies.csv"
```

Use the `rallies.csv` generated for that video. Every data row is analyzed in
file order, including single-impact events. Audio detection, grouping, the input
CSV, and the existing person detector are unchanged.

The command reuses `PlayerDetection.ModelPath`, `FrameIntervalSeconds` (currently
0.5 seconds), `ConfidenceThreshold`, and `NmsIouThreshold`. See
[PLAYER_DETECTION.md](PLAYER_DETECTION.md) for the model setup. A single ONNX
session is loaded for the entire command. Rallies and frames are processed
incrementally; decoded images are disposed after inference.

The new `RallyAnalysis` configuration section controls:

| Setting | Default | Meaning |
|---|---|---|
| `ShortRallyDurationSeconds` | 1.0 | Add context to rallies whose duration is at most this value. Single-impact events always use context. |
| `ContextBeforeSeconds` | 1.0 | Seconds before the original start, clipped at zero. |
| `ContextAfterSeconds` | 1.0 | Seconds after the original end, stopping at the end of available video. |
| `OutputDirectory` | `output/rally-analysis` | Root for a new directory per run. |

Longer rallies sample their original start/end range. Short events sample the
expanded range at the configured interval. Each rally is evaluated independently,
so overlapping context windows may include the same frame in different rallies.
Repeated decoded timestamps within one rally are counted only once.

The console prints each rally index, original and sampling times, decoded frame
timestamps, detection counts, and the final report path. Open the resulting
`rally-analysis.csv` in a text editor or import it into a spreadsheet with comma
as the delimiter. It contains:

- `RallyIndex`: 1-based data row in the input CSV.
- `StartSeconds`, `EndSeconds`, `HitCount`: original, unmodified audio results.
- `SampledFrameCount`: number of frames actually analyzed, including context.
- `ZeroPeopleFrameCount`, `OnePersonFrameCount`, `TwoOrMorePeopleFrameCount`:
  mutually exclusive counts whose sum equals `SampledFrameCount`.
- `AveragePeoplePerFrame`, `MaxPeopleInFrame`: statistics of person detections
  after the existing confidence filter and NMS.
- `FirstSampleTimestampSeconds`, `LastSampleTimestampSeconds`: actual first/last
  decoded source timestamps, retaining the same PTS convention as `detect-players`.

FFmpeg can return a frame slightly after a requested sampling time. Frames beyond
the sampling window are omitted; a zero-duration window retains its first
following frame. Unavailable frames are not counted as zero-person frames. If
no frames are available, frame counts and maximum are zero, while average and
sample timestamps are empty. Check video/CSV alignment in that case. The current
recordings start at zero; sources with nonzero video PTS or audio/video offsets
need aligned rally timestamps, as documented for `detect-players`.

The command saves summary data only. Use `detect-players` to inspect annotated
images. A detection is a person, potentially a spectator or someone on another
court. Missing detections do not prove that a player is outside the frame or
that a rally has ended. Audio impacts may occur outside the camera view. No
rally is classified, rejected, or removed based on these statistics.
