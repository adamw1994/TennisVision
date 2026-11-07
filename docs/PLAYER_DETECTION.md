# Inspecting person detections

From the TennisVision project directory, run:

Replace `RUN_FOLDER` below with the directory of the main pipeline run for this
video, printed as `Results directory`. Each run has its own `rallies.csv`.

```powershell
dotnet run -- detect-players "C:\TennisSamples\Wojtek1set-2min2.mp4" "output\runs\RUN_FOLDER\rallies.csv" 1
```

The final argument is the **1-based data row** of `rallies.csv`, excluding its
header. Use the CSV generated for the selected video. The command reads that
row; it does not run audio analysis or change either existing CSV.

To compare the larger official models on the same rally, pass an optional model path:

```powershell
dotnet run -- detect-players "C:\TennisSamples\Wojtek1set-2min2.mp4" "output\runs\RUN_FOLDER\rallies.csv" 1 "models/yolox_s.onnx"
dotnet run -- detect-players "C:\TennisSamples\Wojtek1set-2min2.mp4" "output\runs\RUN_FOLDER\rallies.csv" 1 "models/yolox_l.onnx"
```

The optional argument overrides `PlayerDetection.ModelPath` for that run. All
models use the same sampling interval, confidence threshold, and NMS settings.

Each run writes a new subdirectory under `output/player-detection/`, named with
the video, model filename, rally row, and UTC run time. Open its PNG files to inspect green person
boxes and confidence scores. Frames with no detections are also saved. The
console prints the full output directory.

`detections.csv` has one row per extracted frame:

- `TimestampSeconds`: actual source frame presentation timestamp reported by
  FFmpeg, not the frame index or time since the rally started.
- `DetectionCount`: zero or more people after confidence filtering and NMS.
- `BoundingBoxes`: JSON array of `[x1,y1,x2,y2]` boxes in original-image pixels,
  measured from the top-left corner, with x increasing right and y downward.
- `ConfidenceScores`: JSON array in the same order as the boxes.
- `FrameFile`: the annotated PNG filename in this directory.

Open the CSV in a text editor or import it into a spreadsheet using comma as the
delimiter and double quote as the text qualifier. Empty detection lists are `[]`.

## Settings

The `PlayerDetection` section of `appsettings.json` controls:

| Setting | Default | Meaning |
|---|---|---|
| `ModelPath` | `models/yolox_l.onnx` | Exact model described below; L is the current default. |
| `FrameIntervalSeconds` | `0.5` | Request frames at rally start plus multiples of this interval. |
| `ConfidenceThreshold` | `0.25` | Lower values retain more possible people. |
| `NmsIouThreshold` | `0.45` | Suppress overlapping person boxes above this intersection-over-union. |
| `OutputDirectory` | `output/player-detection` | Root directory for inspection results. |

Relative model/output paths are resolved from the current working directory.
The settings file is copied to the build output by the existing project setup.
The existing `FFmpegPath` is reused.

FFmpeg chooses the first video frame at or after each requested time. Actual
timestamps may therefore differ slightly from the sampling schedule. Duplicate
frames are skipped. A single-impact rally requests one frame; because its time
range has zero duration, the first following video frame is retained. Frames for
longer groups are kept only within the selected start/end range. No padding is
added to the rally. Timestamp values retain the source video PTS; this matches
the rally timebase for the current recordings, which start at zero. If a source
has a nonzero video start PTS or an audio/video offset, account for that alignment
when preparing rally timestamps.

## Model and manual setup

This implementation supports **the official YOLOX-Nano, YOLOX-S, and YOLOX-L
COCO ONNX models from release `0.1.1rc0`**, with raw, undecoded outputs. Nano
uses 416x416 input (0.91 million parameters); S and L use 640x640 input
(9.0 and 54.2 million parameters). All run on CPU; L uses more memory and
computation. The source project is
[Megvii-BaseDetection/YOLOX](https://github.com/Megvii-BaseDetection/YOLOX), under
the [Apache License 2.0](https://github.com/Megvii-BaseDetection/YOLOX/blob/main/LICENSE).
The official model is linked in the project's
[ONNX Runtime instructions](https://github.com/Megvii-BaseDetection/YOLOX/tree/main/demo/ONNXRuntime).
No training or Python installation is required.

All three models are downloaded in this workspace. For a fresh checkout, download
the desired models manually from the project directory:

```powershell
New-Item -ItemType Directory -Force models | Out-Null
Invoke-WebRequest -Uri "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.onnx" -OutFile "models/yolox_nano.onnx"
Invoke-WebRequest -Uri "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.onnx" -OutFile "models/yolox_s.onnx"
Invoke-WebRequest -Uri "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_l.onnx" -OutFile "models/yolox_l.onnx"
```

Alternatively, download that URL in a browser and set `PlayerDetection.ModelPath`
to the file. The application does not download models automatically. Model
binaries and generated outputs are excluded from Git. Preserve the upstream
license and notices if redistributing the model.

SHA-256 of the downloaded release files:

- Nano: `c789161ed43c8269fcd4e67c67eeeb4e80c622da2eb296a20bc6007bd18a0b7d`
- S: `c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063`
- L: `7860ae79de6c89a3c1eb72ae9a2756c0ccfbe04b7791bb5880afabd97855a411`

## Exact input/output contract

- Input: float32 `[1,3,416,416]` for Nano or `[1,3,640,640]` for S/L, in **BGR**,
  channel-first order. Input size is read and checked from model metadata. Resize preserving
  aspect ratio, place at the top-left, pad the right/bottom with 114. Keep pixel
  values in **0..255**; do not divide by 255 or apply mean/std normalization.
- Output: float32 `[1,3549,85]` for Nano or `[1,8400,85]` for S/L, with grid
  positions across strides 8, 16, and 32. Each row contains raw center offsets, log width/height, objectness,
  and 80 already-sigmoid class scores.
- Decode centers as `(offset + grid) * stride`, dimensions as `exp(value) * stride`.
  Person is COCO class index 0. Its confidence is objectness multiplied by its
  class score. Convert center/size boxes to corners, divide by the resize scale,
  clip to the original image, then apply person-only NMS.

The implementation follows the upstream
[preprocessing](https://github.com/Megvii-BaseDetection/YOLOX/blob/main/yolox/data/data_augment.py),
[ONNX inference](https://github.com/Megvii-BaseDetection/YOLOX/blob/main/demo/ONNXRuntime/onnx_inference.py),
and [grid decoding](https://github.com/Megvii-BaseDetection/YOLOX/blob/main/yolox/utils/demo_utils.py).
Tensor types and shapes are checked when loading. Those checks cannot distinguish
a different export with identical shapes: do not substitute YOLOv5/v8/v10,
legacy preprocessing, or a YOLOX export with decoding/NMS embedded.

This stage reports people, including spectators or people on other courts. It
does not identify either player or interpret a missing detection. Small distant
people may still be missed, even by the larger models. Zero detections never ends or
rejects a rally.
