using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

// Supports the official raw-output YOLOX Nano/S/L 0.1.1rc0 ONNX exports.
internal sealed class PersonDetector : IDisposable
{
    private readonly int inputSize;
    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;

    internal readonly record struct PersonBox(float X1, float Y1, float X2, float Y2, float Confidence);

    public PersonDetector(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("YOLOX model not found. See PLAYER_DETECTION.md for setup.", modelPath);
        // The standard ONNX Runtime package uses CPU execution by default.
        session = new InferenceSession(modelPath);
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
        {
            session.Dispose();
            throw new InvalidDataException("Expected an official single-input, single-output YOLOX export.");
        }
        inputName = session.InputMetadata.Keys.Single();
        outputName = session.OutputMetadata.Keys.Single();
        var input = session.InputMetadata[inputName];
        var output = session.OutputMetadata[outputName];
        inputSize = input.Dimensions.Length == 4 ? input.Dimensions[2] : 0;
        int predictionCount = new[] { 8, 16, 32 }.Sum(stride => (inputSize / stride) * (inputSize / stride));
        if (inputSize is not (416 or 640) || input.ElementType != typeof(float) ||
            !input.Dimensions.SequenceEqual(new[] { 1, 3, inputSize, inputSize }) ||
            output.ElementType != typeof(float) || !output.Dimensions.SequenceEqual(new[] { 1, predictionCount, 85 }))
        {
            session.Dispose();
            throw new InvalidDataException("Unsupported model. Expected float32 [1,3,416,416] -> [1,3549,85] " +
                "or [1,3,640,640] -> [1,8400,85]. Use an official raw-output YOLOX model in PLAYER_DETECTION.md.");
        }
    }

    public List<PersonBox> Detect(SKBitmap image, float confidenceThreshold, float nmsThreshold)
    {
        float scale = Math.Min((float)inputSize / image.Width, (float)inputSize / image.Height);
        int width = Math.Max(1, (int)(image.Width * scale));
        int height = Math.Max(1, (int)(image.Height * scale));
        using var resized = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(resized))
        using (var source = SKImage.FromBitmap(image))
            canvas.DrawImage(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));

        var tensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
        tensor.Buffer.Span.Fill(114);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            SKColor pixel = resized.GetPixel(x, y);
            // Official YOLOX preprocessing: BGR, NCHW, raw 0..255 floats.
            // There is no /255 or mean/std normalization for this export.
            tensor[0, 0, y, x] = pixel.Blue;
            tensor[0, 1, y, x] = pixel.Green;
            tensor[0, 2, y, x] = pixel.Red;
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        Tensor<float> predictions = results.Single(r => r.Name == outputName).AsTensor<float>();
        var candidates = new List<PersonBox>();
        int row = 0;
        foreach (int stride in new[] { 8, 16, 32 })
        for (int gy = 0; gy < inputSize / stride; gy++)
        for (int gx = 0; gx < inputSize / stride; gx++, row++)
        {
            // COCO person is class 0. Scores are already sigmoid probabilities.
            float confidence = predictions[0, row, 4] * predictions[0, row, 5];
            if (!float.IsFinite(confidence) || confidence < confidenceThreshold)
                continue;
            float cx = (predictions[0, row, 0] + gx) * stride;
            float cy = (predictions[0, row, 1] + gy) * stride;
            float bw = MathF.Exp(predictions[0, row, 2]) * stride;
            float bh = MathF.Exp(predictions[0, row, 3]) * stride;
            if (!float.IsFinite(cx) || !float.IsFinite(cy) || !float.IsFinite(bw) || !float.IsFinite(bh))
                continue;
            // Padding is only at the right/bottom, so there is no offset to subtract.
            var box = new PersonBox(
                Math.Clamp((cx - bw / 2) / scale, 0, image.Width),
                Math.Clamp((cy - bh / 2) / scale, 0, image.Height),
                Math.Clamp((cx + bw / 2) / scale, 0, image.Width),
                Math.Clamp((cy + bh / 2) / scale, 0, image.Height), confidence);
            if (box.X2 > box.X1 && box.Y2 > box.Y1)
                candidates.Add(box);
        }

        var kept = new List<PersonBox>();
        foreach (var candidate in candidates.OrderByDescending(b => b.Confidence))
            if (!kept.Any(box => IntersectionOverUnion(box, candidate) > nmsThreshold))
                kept.Add(candidate);
        return kept;
    }

    private static float IntersectionOverUnion(PersonBox a, PersonBox b)
    {
        float intersection = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1)) *
            Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - intersection;
        return union > 0 ? intersection / union : 0;
    }

    public static void SaveAnnotated(SKBitmap image, IReadOnlyList<PersonBox> people, string path)
    {
        using var canvas = new SKCanvas(image);
        using var outline = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        using var background = new SKPaint { Color = SKColors.Black };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var typeface = SKTypeface.FromFamilyName("Arial");
        using var font = new SKFont(typeface, Math.Max(16, image.Width / 90f));
        foreach (var box in people)
        {
            canvas.DrawRect(new SKRect(box.X1, box.Y1, box.X2, box.Y2), outline);
            string label = FormattableString.Invariant($"person {box.Confidence:F2}");
            float textWidth = font.MeasureText(label);
            float labelY = Math.Clamp(box.Y1, font.Size + 6, image.Height);
            float labelX = Math.Clamp(box.X1, 0, Math.Max(0, image.Width - textWidth - 8));
            canvas.DrawRect(labelX, labelY - font.Size - 6, textWidth + 8, font.Size + 6, background);
            canvas.DrawText(label, labelX + 4, labelY - 4, SKTextAlign.Left, font, text);
        }
        canvas.Flush();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        encoded.SaveTo(output);
    }

    public void Dispose() => session.Dispose();
}
