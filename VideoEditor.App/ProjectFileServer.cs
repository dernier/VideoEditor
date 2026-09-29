using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace VideoEditor.App;

/// <summary>
/// Serve i file della cartella del progetto (proxy, miniature, anteprime) all'indirizzo
/// https://project.vedit/…, con il supporto alle richieste parziali (Range) necessario ai video
/// per avanzare e saltare. La cartella può cambiare in qualsiasi momento (apertura di un altro progetto).
/// </summary>
public sealed partial class ProjectFileServer(CoreWebView2 core, string host)
{
    public string? RootDirectory { get; set; }

    public void Attach()
    {
        core.AddWebResourceRequestedFilter($"https://{host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequest;
    }

    private void OnRequest(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (!string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return;
        var env = core.Environment;
        var root = RootDirectory;
        if (root is null)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", Headers("text/plain"));
            return;
        }

        var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", Headers("text/plain"));
            return;
        }

        FileStream fs;
        try
        {
            fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (IOException)
        {
            e.Response = env.CreateWebResourceResponse(null, 503, "Busy", Headers("text/plain"));
            return;
        }

        var length = fs.Length;
        var type = ContentType(full);
        var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
        var m = range is null ? null : RangeRegex().Match(range);
        if (m is { Success: true } && length > 0)
        {
            long start, end;
            if (m.Groups[1].Value.Length == 0)
            {
                // "bytes=-N": ultimi N byte
                var suffix = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                start = Math.Max(0, length - suffix);
                end = length - 1;
            }
            else
            {
                start = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                end = m.Groups[2].Value.Length > 0 ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : length - 1;
            }
            end = Math.Min(end, length - 1);
            if (start > end)
            {
                fs.Dispose();
                e.Response = env.CreateWebResourceResponse(null, 416, "Range Not Satisfiable",
                    Headers(type) + $"\r\nContent-Range: bytes */{length}");
                return;
            }
            fs.Position = start;
            var count = end - start + 1;
            e.Response = env.CreateWebResourceResponse(new SliceStream(fs, count), 206, "Partial Content",
                Headers(type) + $"\r\nContent-Range: bytes {start}-{end}/{length}\r\nContent-Length: {count}");
            return;
        }

        e.Response = env.CreateWebResourceResponse(fs, 200, "OK", Headers(type) + $"\r\nContent-Length: {length}");
    }

    private static string Headers(string contentType) =>
        $"Content-Type: {contentType}\r\nAccept-Ranges: bytes\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache";

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".webm" => "video/webm",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".json" => "application/json",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    [GeneratedRegex(@"bytes=(\d*)-(\d*)")]
    private static partial Regex RangeRegex();

    /// <summary>
    /// Espone solo <c>length</c> byte del flusso sottostante, a partire dalla sua posizione al momento
    /// della creazione. È navigabile (Seek/Position) all'interno dell'intervallo: il WebView2 lo usa.
    /// </summary>
    private sealed class SliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;

        public SliceStream(Stream inner, long length)
        {
            _inner = inner;
            _start = inner.Position;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _inner.Position - _start;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int size)
        {
            var remaining = _length - Position;
            if (remaining <= 0) return 0;
            return _inner.Read(buffer, offset, (int)Math.Min(size, remaining));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                _ => _length + offset,
            };
            target = Math.Clamp(target, 0, _length);
            _inner.Position = _start + target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int size) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
