using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoEditor.Core.Model;
using VideoEditor.Core.Render;

namespace VideoEditor.Core.Media;

/// <summary>Forme d'onda, miniature e proxy (copie leggere riproducibili dall'anteprima dell'editor).</summary>
public static partial class MediaCache
{
    /// <summary>Picchi della forma d'onda al secondo.</summary>
    public const int PeaksPerSecond = 50;

    public sealed record Peaks(int Rate, byte[] Values);

    /// <summary>Calcola (o legge dalla cache) i picchi audio normalizzati 0-255.</summary>
    public static async Task<Peaks?> GetPeaksAsync(ProjectStore store, Asset asset, CancellationToken ct = default)
    {
        if (!asset.HasAudio) return null;
        var path = store.PeaksPath(asset.Id);
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return new Peaks(doc.RootElement.GetProperty("rate").GetInt32(),
                Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString()!));
        }

        const int sampleRate = 4000;
        const int bucket = sampleRate / PeaksPerSecond;
        var values = new List<byte>();
        var src = store.ResolvePath(asset.Path);
        await Ffmpeg.RunBinaryAsync(Ffmpeg.FfmpegPath,
            ["-v", "error", "-i", src, "-vn", "-ac", "1", "-ar", sampleRate.ToString(), "-f", "s16le", "-"],
            async stream =>
            {
                var buf = new byte[bucket * 2 * 256];
                var carry = 0;
                var inBucket = 0;
                var peak = 0;
                while (true)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(carry), ct);
                    if (n == 0) break;
                    n += carry;
                    var even = n - n % 2;
                    for (var i = 0; i < even; i += 2)
                    {
                        var s = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(buf.AsSpan(i)));
                        if (s > peak) peak = s;
                        if (++inBucket == bucket)
                        {
                            values.Add(ToByte(peak));
                            inBucket = 0;
                            peak = 0;
                        }
                    }
                    carry = n - even;
                    if (carry > 0) buf[0] = buf[even];
                }
                if (inBucket > 0) values.Add(ToByte(peak));
            }, ct);

        var result = new Peaks(PeaksPerSecond, values.ToArray());
        Directory.CreateDirectory(store.CacheDir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { rate = result.Rate, data = Convert.ToBase64String(result.Values) }), ct);
        return result;
    }

    // Scala con radice quadrata: le parti a basso volume restano visibili.
    private static byte ToByte(int peak) => (byte)Math.Round(Math.Sqrt(Math.Min(peak, 32767) / 32767.0) * 255);

    /// <summary>Crea la miniatura di un asset video o immagine.</summary>
    public static async Task<string?> EnsureThumbnailAsync(ProjectStore store, Asset asset, CancellationToken ct = default)
    {
        if (!asset.HasVideo) return null;
        var path = store.ThumbPath(asset.Id);
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(store.CacheDir);
        var at = asset.Kind == AssetKind.Image ? 0 : Math.Min(1, asset.Duration / 2);
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath,
            ["-v", "error", "-y", "-ss", Ffmpeg.Sec(at), "-i", store.ResolvePath(asset.Path), "-frames:v", "1",
             "-vf", "scale=320:-2", "-q:v", "4", path], ct: ct);
        return r.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Crea il proxy usato dall'anteprima dell'editor: H.264 a 720p (o AAC per l'audio, JPEG per le immagini),
    /// così anche HEVC a 10 bit o ProRes si possono riprodurre nel WebView2.
    /// </summary>
    public static async Task<string> EnsureProxyAsync(ProjectStore store, Asset asset, string encoder = "auto",
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var path = store.ProxyPath(asset.Id, asset.Kind);
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(store.ProxyDir);
        var tmp = Path.Combine(store.ProxyDir, "tmp-" + Path.GetFileName(path));
        var src = store.ResolvePath(asset.Path);
        List<string> args = ["-v", "error", "-y", "-progress", "pipe:1", "-nostats", "-i", src];
        switch (asset.Kind)
        {
            case AssetKind.Image:
                args.AddRange(["-frames:v", "1", "-vf", "scale='min(1920,iw)':-2", "-q:v", "3", "-f", "image2"]);
                break;
            case AssetKind.Audio:
                args.AddRange(["-vn", "-c:a", "aac", "-b:a", "160k", "-f", "mp4"]);
                break;
            default:
                var enc = Encoders.Resolve(encoder);
                args.AddRange(["-vf", "scale=-2:'min(720,ih)',format=yuv420p"]);
                args.AddRange(Encoders.ProxyArgs(enc));
                args.AddRange(asset.HasAudio ? ["-c:a", "aac", "-b:a", "128k", "-ac", "2"] : ["-an"]);
                args.AddRange(["-movflags", "+faststart", "-f", "mp4"]);
                break;
        }
        args.Add(tmp);

        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, args, onStdout: line =>
        {
            if (progress is not null && asset.Duration > 0 && ParseProgressTime(line) is double t)
                progress.Report(Math.Clamp(t / asset.Duration, 0, 1));
        }, ct: ct);
        if (r.ExitCode != 0 && asset.Kind == AssetKind.Video && Encoders.IsNvenc(Encoders.Resolve(encoder)))
        {
            // NVENC non disponibile (driver, sessioni esaurite...): si riprova in software.
            return await EnsureProxyAsync(store, asset, "libx264", progress, ct);
        }
        if (r.ExitCode != 0) throw new InvalidOperationException($"Creazione del proxy fallita per {asset.Id}: {LastLines(r.StdErr)}");
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public sealed record Silence(double Start, double End)
    {
        public double Duration => End - Start;
    }

    /// <summary>Trova le pause (tratti sotto la soglia <paramref name="noiseDb"/> per almeno <paramref name="minDuration"/> s).</summary>
    public static async Task<List<Silence>> DetectSilencesAsync(ProjectStore store, Asset asset, double noiseDb = -35, double minDuration = 0.5, CancellationToken ct = default)
    {
        if (!asset.HasAudio) throw new InvalidOperationException($"L'asset {asset.Id} non ha audio.");
        var result = new List<Silence>();
        double? start = null;
        var filter = string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={noiseDb}dB:d={minDuration}");
        await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", store.ResolvePath(asset.Path), "-vn", "-af", filter, "-f", "null", "-"],
            onStderr: line =>
            {
                var m = SilenceStart().Match(line);
                if (m.Success) { start = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); return; }
                m = SilenceEnd().Match(line);
                if (m.Success && start is double s)
                {
                    lock (result) result.Add(new Silence(Math.Max(0, s), double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));
                    start = null;
                }
            }, ct: ct);
        if (start is double open && asset.Duration - open >= minDuration) result.Add(new Silence(open, asset.Duration));
        return result;
    }

    internal static double? ParseProgressTime(string line)
    {
        // "-progress" scrive righe chiave=valore; out_time_us è in microsecondi.
        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
            return us / 1_000_000.0;
        return null;
    }

    internal static string LastLines(string text, int count = 6) =>
        string.Join('\n', text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(count)).Trim();

    [GeneratedRegex(@"silence_start:\s*(-?[\d.]+)")]
    private static partial Regex SilenceStart();

    [GeneratedRegex(@"silence_end:\s*([\d.]+)")]
    private static partial Regex SilenceEnd();
}
