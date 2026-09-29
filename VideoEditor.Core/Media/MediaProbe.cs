using System.Globalization;
using System.Text.Json;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Media;

/// <summary>Legge le caratteristiche di un file multimediale con ffprobe.</summary>
public static class MediaProbe
{
    private static readonly HashSet<string> ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tif", ".tiff"];

    public static Asset Probe(string fullPath)
    {
        if (!File.Exists(fullPath)) throw new FileNotFoundException($"File non trovato: {fullPath}");
        var r = Ffmpeg.Run(Ffmpeg.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", fullPath]);
        if (r.ExitCode != 0) throw new InvalidDataException($"ffprobe non riesce a leggere {fullPath}: {r.StdErr.Trim()}");
        return Parse(r.StdOut, fullPath);
    }

    internal static Asset Parse(string ffprobeJson, string fullPath)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;
        var asset = new Asset { Name = Path.GetFileName(fullPath) };
        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.GetProperty("codec_type").GetString();
                var attached = s.TryGetProperty("disposition", out var disp) &&
                               disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video is null && !attached) video = s;
                else if (type == "audio" && audio is null) audio = s;
            }
        }

        double duration = 0;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            duration = ParseDouble(d.GetString());
        if (duration <= 0 && video is { } vs && vs.TryGetProperty("duration", out var vd)) duration = ParseDouble(vd.GetString());
        if (duration <= 0 && audio is { } aus && aus.TryGetProperty("duration", out var ad)) duration = ParseDouble(ad.GetString());

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var codec = video?.GetProperty("codec_name").GetString();
        var isImage = ImageExtensions.Contains(ext) || codec is "png" or "mjpeg" or "bmp" or "webp" && duration <= 0.1;

        if (video is { } v)
        {
            asset.HasVideo = true;
            asset.Width = v.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            asset.Height = v.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            asset.Codec = codec;
            asset.Fps = v.TryGetProperty("avg_frame_rate", out var fr) ? ParseRate(fr.GetString()) : 0;
            if (asset.Fps <= 0 && v.TryGetProperty("r_frame_rate", out var rfr)) asset.Fps = ParseRate(rfr.GetString());
            var pix = v.TryGetProperty("pix_fmt", out var pf) ? pf.GetString() ?? "" : "";
            var alphaTag = v.TryGetProperty("tags", out var tags) && tags.TryGetProperty("alpha_mode", out var am) && am.GetString() == "1";
            asset.HasAlpha = alphaTag || pix.Contains('a') && (pix.StartsWith("yuva") || pix.StartsWith("rgba") || pix.StartsWith("argb") || pix.StartsWith("bgra") || pix.StartsWith("gbrap") || pix == "ya8");
        }
        asset.HasAudio = audio is not null;
        asset.Kind = isImage ? AssetKind.Image : asset.HasVideo ? AssetKind.Video : AssetKind.Audio;
        if (asset.Kind == AssetKind.Image)
        {
            asset.HasAudio = false;
            duration = 0;
        }
        else if (!asset.HasVideo && !asset.HasAudio)
        {
            throw new InvalidDataException($"{fullPath} non contiene né video né audio.");
        }
        asset.Duration = Math.Round(duration, 3);
        return asset;
    }

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseRate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var parts = s.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
            return Math.Round(n / dd, 3);
        return ParseDouble(s);
    }
}
