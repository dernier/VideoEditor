using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Render;

public sealed class RenderOptions
{
    /// <summary>File di uscita. Se relativo è rispetto alla cartella del progetto.</summary>
    public string? Output { get; init; }

    public string Encoder { get; init; } = "auto";

    /// <summary>CQ/CRF: più basso = qualità migliore e file più grande.</summary>
    public int? Quality { get; init; }

    /// <summary>Anteprima veloce a bassa risoluzione.</summary>
    public bool Preview { get; init; }

    public int? Height { get; init; }
    public double From { get; init; }
    public double? To { get; init; }
}

public sealed record RenderResult(string Output, string Encoder, double Duration, TimeSpan Elapsed);

/// <summary>Esegue ffmpeg sul filtergraph prodotto da <see cref="RenderGraphBuilder"/>.</summary>
public static class Renderer
{
    public static async Task<RenderResult> RenderAsync(ProjectStore store, Project project, RenderOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var encoder = Encoders.Resolve(options.Encoder);
        var output = options.Output is null
            ? Path.Combine(store.OutputDir, options.Preview ? "anteprima.mp4" : $"{Safe(project.Name)}.mp4")
            : Path.IsPathRooted(options.Output) ? options.Output : Path.Combine(store.Directory, options.Output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);

        var request = new RenderRequest
        {
            From = options.From,
            To = options.To,
            Height = options.Height ?? (options.Preview ? Math.Min(720, project.Settings.Height) : null),
        };
        var workDir = CreateWorkDir(store);
        try
        {
            var graph = Prepare(store, project, request, workDir);
            var tmpOut = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileNameWithoutExtension(output) + ".part" + Path.GetExtension(output));

            async Task<Ffmpeg.Result> Run(string enc)
            {
                var args = BaseArgs(graph, workDir);
                args.AddRange(options.Preview ? Encoders.PreviewArgs(enc) : Encoders.FinalArgs(enc, options.Quality));
                args.AddRange(["-c:a", "aac", "-b:a", options.Preview ? "128k" : "192k", "-ar", project.Settings.SampleRate.ToString()]);
                args.AddRange(["-movflags", "+faststart", "-f", "mp4", tmpOut]);
                return await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, args, workDir, line =>
                {
                    if (MediaCache.ParseProgressTime(line) is double t)
                        progress?.Report(Math.Clamp(t / graph.Duration, 0, 1));
                }, ct: ct);
            }

            var result = await Run(encoder);
            if (result.ExitCode != 0 && Encoders.IsNvenc(encoder))
            {
                // Driver o GPU non disponibili: si ripiega sulla codifica software.
                encoder = Encoders.SoftwareFallback(encoder);
                result = await Run(encoder);
            }
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Rendering fallito:\n" + MediaCache.LastLines(result.StdErr, 12));
            File.Move(tmpOut, output, overwrite: true);
            progress?.Report(1);
            return new RenderResult(output, encoder, graph.Duration, DateTime.UtcNow - started);
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>Esporta il fotogramma composto all'istante <paramref name="time"/> in un'immagine PNG/JPEG.</summary>
    public static async Task<string> RenderFrameAsync(ProjectStore store, Project project, double time, string output,
        int? height = null, CancellationToken ct = default, bool grid = false)
    {
        var fps = project.Settings.Fps > 0 ? project.Settings.Fps : 30;
        var duration = project.GetDuration();
        time = Math.Clamp(time, 0, Math.Max(0, duration - 1 / fps));
        var request = new RenderRequest { From = time, To = time + 1 / fps, Height = height, Audio = false, Grid = grid };
        var workDir = CreateWorkDir(store);
        try
        {
            var graph = Prepare(store, project, request, workDir);
            var args = BaseArgs(graph, workDir);
            args.AddRange(["-frames:v", "1", "-update", "1", output]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath, args, workDir, ct: ct);
            if (r.ExitCode != 0) throw new InvalidOperationException("Estrazione del fotogramma fallita:\n" + MediaCache.LastLines(r.StdErr, 12));
            return output;
        }
        finally
        {
            TryDeleteDir(workDir);
        }
    }

    /// <summary>Costruisce il filtergraph e scrive i file di appoggio nella cartella di lavoro.</summary>
    public static RenderGraph Prepare(ProjectStore store, Project project, RenderRequest request, string workDir)
    {
        var builder = new RenderGraphBuilder(project, store.ResolvePath, ResolveFont(project));
        var graph = builder.Build(request);
        foreach (var (name, content) in graph.TextFiles)
            File.WriteAllText(Path.Combine(workDir, name), content);
        File.WriteAllText(Path.Combine(workDir, "graph.txt"), graph.Filter);
        return graph;
    }

    private static List<string> BaseArgs(RenderGraph graph, string workDir)
    {
        List<string> args = ["-hide_banner", "-y", "-v", "error", "-progress", "pipe:1", "-nostats"];
        args.AddRange(graph.InputArgs);
        // Il filtergraph va in un file: con molte clip supererebbe il limite della riga di comando.
        args.AddRange(Ffmpeg.GetVersion().Major >= 7
            ? ["-/filter_complex", Path.Combine(workDir, "graph.txt")]
            : ["-filter_complex_script", Path.Combine(workDir, "graph.txt")]);
        if (graph.VideoLabel is not null) args.AddRange(["-map", $"[{graph.VideoLabel}]"]);
        if (graph.AudioLabel is not null) args.AddRange(["-map", $"[{graph.AudioLabel}]"]);
        return args;
    }

    /// <summary>Font per i testi: quello del progetto, altrimenti uno di sistema.</summary>
    public static string? ResolveFont(Project project)
    {
        if (!string.IsNullOrWhiteSpace(project.Settings.FontFile) && File.Exists(project.Settings.FontFile))
            return project.Settings.FontFile;
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Windows\Fonts\arialbd.ttf", @"C:\Windows\Fonts\arial.ttf", @"C:\Windows\Fonts\segoeui.ttf"]
            : ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
               "/System/Library/Fonts/Supplemental/Arial.ttf"];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CreateWorkDir(ProjectStore store)
    {
        var dir = Path.Combine(store.TempDir, "render-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "export" : s;
    }
}
