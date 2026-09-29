using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace VideoEditor.Core.Transcription;

/// <summary>
/// Trascrizione locale con Whisper (whisper.cpp tramite Whisper.net), con i tempi di ogni parola.
/// Usa la GPU con CUDA o Vulkan se disponibili, altrimenti la CPU.
/// </summary>
public static class WhisperTranscriber
{
    public static readonly Dictionary<string, GgmlType> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"] = GgmlType.Tiny,
        ["base"] = GgmlType.Base,
        ["small"] = GgmlType.Small,
        ["medium"] = GgmlType.Medium,
        ["large-v3"] = GgmlType.LargeV3,
        ["large-v3-turbo"] = GgmlType.LargeV3Turbo,
    };

    public const string DefaultModel = "large-v3-turbo";

    /// <summary>Cartella dei modelli (%LOCALAPPDATA%\VideoEditor\models o VEDIT_MODELS).</summary>
    public static string ModelsDir =>
        Environment.GetEnvironmentVariable("VEDIT_MODELS") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "models");

    /// <summary>Scarica il modello la prima volta (da Hugging Face) e restituisce il percorso del file.</summary>
    public static async Task<string> EnsureModelAsync(string model, Action<string>? log = null, CancellationToken ct = default)
    {
        if (File.Exists(model)) return model;
        if (!Models.TryGetValue(model, out var type))
            throw new ArgumentException($"Modello sconosciuto: {model}. Validi: {string.Join(", ", Models.Keys)} o il percorso di un file .bin.");
        Directory.CreateDirectory(ModelsDir);
        var path = Path.Combine(ModelsDir, $"ggml-{model}.bin");
        if (File.Exists(path)) return path;
        log?.Invoke($"Scarico il modello Whisper '{model}' (solo la prima volta)...");
        var tmp = path + ".download";
        await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, QuantizationType.NoQuantization, ct))
        await using (var dst = File.Create(tmp))
        {
            await src.CopyToAsync(dst, ct);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Trascrive l'audio di un asset e restituisce parole e frasi (tempi nel file sorgente).</summary>
    public static async Task<Transcript> TranscribeAsync(ProjectStore store, Asset asset, string model = DefaultModel,
        string? language = "it", string? prompt = null, Action<string>? log = null, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!asset.HasAudio) throw new InvalidOperationException($"L'asset {asset.Id} non ha audio.");
        var modelPath = await EnsureModelAsync(model, log, ct);

        // Whisper vuole WAV mono a 16 kHz.
        Directory.CreateDirectory(store.TempDir);
        var wav = Path.Combine(store.TempDir, $"{asset.Id}-16k.wav");
        log?.Invoke("Estraggo l'audio...");
        var r = await Ffmpeg.RunAsync(Ffmpeg.FfmpegPath,
            ["-v", "error", "-y", "-i", store.ResolvePath(asset.Path), "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav], ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException("Estrazione audio fallita: " + MediaCache.LastLines(r.StdErr));

        try
        {
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            using var factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
            log?.Invoke($"Motore: {RuntimeOptions.LoadedLibrary?.ToString() ?? "?"} — trascrizione in corso...");

            // Un segmento per parola: SplitOnWord + lunghezza massima 1 (come "whisper-cli -ml 1").
            var builder = factory.CreateBuilder()
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(1)
                .WithProbabilities();
            builder = string.IsNullOrEmpty(language) || language == "auto"
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(language);
            if (!string.IsNullOrWhiteSpace(prompt)) builder = builder.WithPrompt(prompt);
            if (progress is not null) builder = builder.WithProgressHandler(p => progress.Report(p / 100.0));
            await using var processor = builder.Build();

            var words = new List<Word>();
            string? detected = null;
            await using var stream = File.OpenRead(wav);
            await foreach (var seg in processor.ProcessAsync(stream, ct))
            {
                detected ??= seg.Language;
                var trimmed = seg.Text.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('[') && trimmed.EndsWith(']')) continue;
                words.Add(new Word
                {
                    Text = seg.Text,
                    Start = Math.Round(seg.Start.TotalSeconds, 3),
                    End = Math.Round(seg.End.TotalSeconds, 3),
                    P = Math.Round(seg.Probability, 3),
                });
            }

            var transcript = new Transcript
            {
                AssetId = asset.Id,
                Language = detected ?? language ?? "",
                Model = model,
                Words = MergeFragments(words),
            };
            transcript.Segments = TranscriptTools.BuildSegments(transcript.Words);
            return transcript;
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Whisper a volte spezza una parola in più token (es. " perch" + "é"): un frammento che non
    /// inizia con uno spazio viene unito alla parola precedente. Il testo finale è senza spazi ai lati.
    /// </summary>
    internal static List<Word> MergeFragments(List<Word> raw)
    {
        var result = new List<Word>();
        foreach (var w in raw)
        {
            var text = w.Text;
            var isPunct = text.Trim().All(char.IsPunctuation);
            if (result.Count > 0 && (isPunct || !char.IsWhiteSpace(text[0])))
            {
                var prev = result[^1];
                prev.Text += text.Trim();
                if (!isPunct) prev.End = Math.Max(prev.End, w.End);
                prev.P = Math.Min(prev.P, w.P);
                continue;
            }
            result.Add(new Word { Text = text.Trim(), Start = w.Start, End = w.End, P = w.P });
        }
        return result;
    }
}
