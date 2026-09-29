using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoEditor.Core.Media;

/// <summary>Individua ffmpeg/ffprobe e ne esegue i comandi.</summary>
public static partial class Ffmpeg
{
    private static string? _ffmpeg, _ffprobe;
    private static Version? _version;
    private static HashSet<string>? _encoders;

    public static string FfmpegPath => _ffmpeg ??= Locate("ffmpeg", "VEDIT_FFMPEG");
    public static string FfprobePath => _ffprobe ??= Locate("ffprobe", "VEDIT_FFPROBE");

    /// <summary>
    /// Cerca l'eseguibile: variabile d'ambiente, cartella dell'applicazione (anche <c>ffmpeg\bin</c>), PATH.
    /// </summary>
    public static string Locate(string name, string envVar)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, exe), Path.Combine(baseDir, "ffmpeg", "bin", exe), Path.Combine(baseDir, "ffmpeg", exe) })
            if (File.Exists(candidate)) return candidate;
        var found = FindOnPath(exe);
        return found ?? throw new FileNotFoundException(
            $"{name} non trovato. Installa ffmpeg (es. 'winget install Gyan.FFmpeg') e aggiungilo al PATH, " +
            $"oppure imposta la variabile {envVar}.");
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Versione principale di ffmpeg (serve per scegliere la sintassi di alcune opzioni).</summary>
    public static Version GetVersion()
    {
        if (_version is not null) return _version;
        var r = Run(FfmpegPath, ["-hide_banner", "-version"]);
        var m = VersionRegex().Match(r.StdOut);
        _version = m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(7, 0);
        return _version;
    }

    public static bool HasEncoder(string name)
    {
        if (_encoders is null)
        {
            var r = Run(FfmpegPath, ["-hide_banner", "-encoders"]);
            _encoders = r.StdOut.Split('\n')
                .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2 && p[0].Length == 6)
                .Select(p => p[1])
                .ToHashSet();
        }
        return _encoders.Contains(name);
    }

    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>Esecuzione sincrona con cattura dell'output (per comandi brevi).</summary>
    public static Result Run(string exe, IEnumerable<string> args, string? workDir = null)
    {
        using var p = Start(exe, args, workDir, redirectStdout: true);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return new Result(p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// Esecuzione asincrona: ogni riga di stdout/stderr viene passata ai callback. In caso di
    /// annullamento il processo viene terminato.
    /// </summary>
    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string? workDir = null,
        Action<string>? onStdout = null, Action<string>? onStderr = null, CancellationToken ct = default)
    {
        using var p = Start(exe, args, workDir, redirectStdout: true);
        var errTail = new StringBuilder();
        var outAll = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line) { outAll.AppendLine(line); onStdout?.Invoke(line); }
        }, ct);
        var readErr = Task.Run(async () =>
        {
            while (await p.StandardError.ReadLineAsync(ct) is { } line)
            {
                lock (errTail)
                {
                    errTail.AppendLine(line);
                    if (errTail.Length > 64_000) errTail.Remove(0, errTail.Length - 32_000);
                }
                onStderr?.Invoke(line);
            }
        }, ct);
        try
        {
            await p.WaitForExitAsync(ct);
            await Task.WhenAll(readOut, readErr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return new Result(p.ExitCode, outAll.ToString(), errTail.ToString());
    }

    /// <summary>Esegue il comando leggendo stdout come flusso binario (es. campioni audio grezzi).</summary>
    public static async Task<int> RunBinaryAsync(string exe, IEnumerable<string> args, Func<Stream, Task> readStdout, CancellationToken ct = default)
    {
        using var p = Start(exe, args, null, redirectStdout: true);
        var drainErr = p.StandardError.ReadToEndAsync(ct);
        await readStdout(p.StandardOutput.BaseStream);
        await p.WaitForExitAsync(ct);
        await drainErr;
        return p.ExitCode;
    }

    private static Process Start(string exe, IEnumerable<string> args, string? workDir, bool redirectStdout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = redirectStdout ? Encoding.UTF8 : null,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Impossibile avviare {exe}.");
        p.StandardInput.Close();
        return p;
    }

    /// <summary>Formatta un tempo per la riga di comando di ffmpeg (secondi con punto decimale).</summary>
    public static string Sec(double seconds) => Math.Max(0, seconds).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex(@"ffmpeg version n?(\d+)\.(\d+)")]
    private static partial Regex VersionRegex();
}
