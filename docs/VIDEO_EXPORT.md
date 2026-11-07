# MP4 to highlights MP4

Run from the project directory with just the source video:

```powershell
dotnet run -- "C:\TennisSamples\Wojtek1set-2min2.mp4"
```

Each run creates a unique directory under `output/runs/` containing:

- `impacts.csv`: detected impact times and existing diagnostic measurements.
- `rallies.csv`: grouped impact times (`StartSeconds,EndSeconds,HitCount`).
- `Wojtek1set-2min2-highlights.mp4`: the selected video intervals.

The directory name contains the source video name, UTC run time, and a unique ID.
The console prints its full path. It is safe to rerun the same input; previous
results remain in their own directories. No results are written beside the source.
The same command accepts a full recording, including videos of 30 minutes or longer:

```powershell
dotnet run -- "C:\TennisSamples\Wojtek1set.mp4"
```

Its MP4 is named `Wojtek1set-highlights.mp4` inside that run's new directory.
The original source is never replaced.

## Pipeline

1. FFprobe reads the source duration and stream start times.
2. The existing FFmpeg/audio detector analyzes the source during this run,
   streaming mono PCM audio without retaining the recording in memory.
3. Detected timestamps are collected in memory and passed directly to the
   existing `RallyDetector`. No input CSV is needed.
4. Every candidate rally, including single impacts, gets padding. Ranges are
   clamped to the video duration, sorted, and merged if they overlap or their
   remaining gap is at most the configured merge gap. Merged gaps are retained.
5. FFmpeg reads the selected ranges chronologically through a temporary concat
   manifest. Timestamp-based `concatdec_select` video/audio filters discard
   decoded content outside the cuts, including keyframe preroll. Both streams
   retain the shared concat timeline, and audio resampling handles small boundary
   gaps without cumulative drift. Cutting is at video/audio frame boundaries.
6. A single encoding pass produces H.264 (`libx264`, veryfast, CRF 23, yuv420p)
   and 48 kHz AAC (192 kbit/s), with MP4 fast-start enabled. These settings are
   consistent across all intervals. The source video frame timing is retained
   using variable-frame-rate output; audio is not independently shifted.
7. The completed MP4 is probed for its actual duration, then moved to the final
   filename. Temporary output and the manifest are cleaned up, including on
   ordinary failures. No temporary segment videos are generated.

Only detections and intervals are retained in managed memory. FFmpeg streams
media from disk; it does not load the full recording into RAM. Encoding a long
4K recording can still take considerable CPU time and disk space.

`VideoExport` in `appsettings.json` contains:

| Setting | Default | Meaning |
|---|---|---|
| `FFprobePath` | `ffprobe` | Executable from the same FFmpeg installation; a full path is allowed. |
| `OutputDirectory` | `output/runs` | Root directory for a new folder per run, relative to the working directory. |
| `PreRollSeconds` | 2 | Padding before each candidate rally. |
| `PostRollSeconds` | 3 | Padding after each candidate rally. |
| `MergeGapSeconds` | 1 | Maximum gap between padded intervals that should be merged. |

`FFmpegPath` is reused. Durations must be finite and nonnegative. If both padding
values are zero, a single-impact event retains a one-frame-sized interval.
Impact times are mapped from decoded-audio time to the source timeline using the
reported audio/format start-time offset. Existing diagnostic timestamps are not
changed.

The command writes `impacts.csv` and `rallies.csv` in the run's result folder.
Those are outputs, never inputs to this pipeline. Each run keeps its own copy.
The existing `detect-players` and `analyze-rallies` commands remain available.
The single-file export command does not load an ONNX inference session, run
person detection, filter by hit count, or classify candidate rallies.

The console reports source duration, impact count, rally count, merged interval
count, encoding progress, measured output duration, percentage retained, audio
analysis time, export time, and total processing time. Container/frame rounding
can make actual output duration differ slightly from the sum of cut intervals.
If no rallies are detected, diagnostics are written and the command finishes
successfully with zero output duration; no empty MP4 is created.

The concat filtering approach follows FFmpeg's documented
[`concatdec_select` example](https://github.com/FFmpeg/FFmpeg/blob/master/doc/filters.texi)
and [concat demuxer](https://github.com/FFmpeg/FFmpeg/blob/master/doc/demuxers.texi).
