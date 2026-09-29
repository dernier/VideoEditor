using System;
using System.Collections.Generic;

namespace VideoEditor.Core.Render;

/// <summary>Parametri degli encoder video. Con una GPU NVIDIA si usano gli encoder NVENC.</summary>
public static class Encoders
{
    public static readonly string[] Known = ["auto", "h264_nvenc", "hevc_nvenc", "av1_nvenc", "libx264", "libx265"];

    /// <summary>"auto" diventa h264_nvenc se ffmpeg lo supporta, altrimenti libx264.</summary>
    public static string Resolve(string? encoder)
    {
        if (string.IsNullOrWhiteSpace(encoder) || encoder == "auto")
            return Media.Ffmpeg.HasEncoder("h264_nvenc") ? "h264_nvenc" : "libx264";
        if (!Known.Contains(encoder)) throw new ArgumentException($"Encoder sconosciuto: {encoder}. Validi: {string.Join(", ", Known)}.");
        return encoder;
    }

    public static bool IsNvenc(string encoder) => encoder.EndsWith("_nvenc", StringComparison.Ordinal);

    public static string SoftwareFallback(string encoder) => encoder switch
    {
        "hevc_nvenc" => "libx265",
        _ => "libx264",
    };

    /// <summary>Argomenti per l'esportazione finale. <paramref name="quality"/> è CQ/CRF (più basso = migliore).</summary>
    public static List<string> FinalArgs(string encoder, int? quality = null) => encoder switch
    {
        "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", Q(quality, 19), "-b:v", "0",
                         "-profile:v", "high", "-pix_fmt", "yuv420p"],
        "hevc_nvenc" => ["-c:v", "hevc_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", Q(quality, 21), "-b:v", "0",
                         "-tag:v", "hvc1", "-pix_fmt", "yuv420p"],
        "av1_nvenc" => ["-c:v", "av1_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", Q(quality, 30), "-b:v", "0",
                        "-pix_fmt", "yuv420p"],
        "libx265" => ["-c:v", "libx265", "-preset", "medium", "-crf", Q(quality, 22), "-tag:v", "hvc1", "-pix_fmt", "yuv420p"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-crf", Q(quality, 18), "-pix_fmt", "yuv420p"],
    };

    /// <summary>Anteprima veloce: qualità bassa, codifica rapida, sempre H.264 (riproducibile nel WebView2).</summary>
    public static List<string> PreviewArgs(string encoder) => IsNvenc(encoder)
        ? ["-c:v", "h264_nvenc", "-preset", "p1", "-rc", "vbr", "-cq", "28", "-b:v", "0", "-pix_fmt", "yuv420p"]
        : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p"];

    public static List<string> ProxyArgs(string encoder) => IsNvenc(encoder)
        ? ["-c:v", "h264_nvenc", "-preset", "p2", "-rc", "vbr", "-cq", "26", "-b:v", "0", "-g", "30"]
        : ["-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-g", "30"];

    private static string Q(int? q, int def) => (q ?? def).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
