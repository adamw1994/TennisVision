using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

internal sealed class VideoFrameExtractor(string ffmpegPath)
{
    public async Task<(SKBitmap Image, double TimestampSeconds)?> ExtractAsync(string videoPath, double seconds)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Accurate input seeking discards earlier frames. copyts preserves source PTS for showinfo.
        string[] arguments = ["-hide_banner", "-loglevel", "info", "-nostdin", "-copyts",
            "-ss", seconds.ToString("G17", CultureInfo.InvariantCulture), "-i", videoPath,
            "-map", "0:v:0", "-an", "-sn", "-dn", "-vf", "showinfo",
            "-frames:v", "1", "-fps_mode", "passthrough", "-c:v", "png", "-f", "image2pipe", "pipe:1"];
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start FFmpeg.");
        Task<string> errorsTask = process.StandardError.ReadToEndAsync();
        using var png = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(png);
        await process.WaitForExitAsync();
        string errors = await errorsTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Frame extraction failed: {errors}");
        if (png.Length == 0)
            return null;

        Match timestamp = Regex.Match(errors, @"\bn:\s*0\s+pts:.*?\bpts_time:([-+\d.eE]+)");
        if (!timestamp.Success)
            throw new InvalidDataException("FFmpeg did not report the decoded frame timestamp.");

        var image = SKBitmap.Decode(png.ToArray())
            ?? throw new InvalidDataException("FFmpeg output is not a readable PNG.");
        return (image, double.Parse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture));
    }
}
