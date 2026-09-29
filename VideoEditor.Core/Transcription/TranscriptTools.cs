using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Transcription;

/// <summary>Frasi, mappatura sulla timeline, formati leggibili (testo, SRT) e ricerche nella trascrizione.</summary>
public static class TranscriptTools
{
    /// <summary>Intercalari italiani (e qualche inglese) da proporre per il taglio.</summary>
    public static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ehm", "eh", "ehh", "ehhm", "uhm", "um", "umm", "uh", "mh", "mmh", "mm", "mmm", "ah", "ahm", "hmm", "boh",
    };

    public static List<Segment> BuildSegments(IReadOnlyList<Word> words, double maxGap = 0.8, int maxWords = 25)
    {
        var segments = new List<Segment>();
        var current = new List<Word>();
        void Flush()
        {
            if (current.Count == 0) return;
            segments.Add(new Segment
            {
                Start = current[0].Start,
                End = current[^1].End,
                Text = string.Join(' ', current.Select(w => w.Text)),
            });
            current.Clear();
        }
        foreach (var w in words)
        {
            if (current.Count > 0 && (w.Start - current[^1].End > maxGap || current.Count >= maxWords)) Flush();
            current.Add(w);
            if (w.Text.EndsWith('.') || w.Text.EndsWith('?') || w.Text.EndsWith('!')) Flush();
        }
        Flush();
        return segments;
    }

    /// <summary>
    /// Riporta le parole sulla timeline corrente: per ogni clip (non muta) di un asset trascritto
    /// si prendono le parole che cadono nel tratto usato, spostate alla posizione della clip.
    /// </summary>
    public static List<TimelineWord> ToTimeline(Project project, Func<string, Transcript?> loadTranscript, bool includeDucked = false)
    {
        var cache = new Dictionary<string, Transcript?>();
        var result = new List<TimelineWord>();
        foreach (var track in project.Tracks)
        {
            if (track.Kind == TrackKind.Text || track.Muted || track.Duck && !includeDucked) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.AssetId is null) continue;
                if (!cache.TryGetValue(clip.AssetId, out var t)) cache[clip.AssetId] = t = loadTranscript(clip.AssetId);
                if (t is null) continue;
                var srcEnd = clip.In + clip.Duration;
                foreach (var w in t.Words)
                {
                    var mid = (w.Start + w.End) / 2;
                    if (mid < clip.In || mid >= srcEnd) continue;
                    var start = Math.Max(clip.Start, clip.Start + (w.Start - clip.In));
                    var end = Math.Min(clip.End, clip.Start + (w.End - clip.In));
                    result.Add(new TimelineWord(w.Text, Math.Round(start, 3), Math.Round(end, 3), w.P, clip.Id, clip.AssetId));
                }
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    public static List<Word> ToWords(IEnumerable<TimelineWord> words) =>
        words.Select(w => new Word { Text = w.Text, Start = w.Start, End = w.End, P = w.P }).ToList();

    /// <summary>
    /// Testo leggibile per l'analisi: una riga per frase con i tempi, pause lunghe e parole
    /// incerte segnalate in linea. È il formato pensato per Claude.
    /// </summary>
    public static string FormatReadable(IReadOnlyList<Word> words, double pauseThreshold = 0.5, double lowConfidence = 0.4)
    {
        var sb = new StringBuilder();
        Word? prev = null;
        foreach (var seg in BuildSegments(words))
        {
            // Pausa tra la frase precedente e questa: su una riga a sé.
            if (prev is not null && seg.Start - prev.End >= pauseThreshold)
                sb.AppendLine($"⟨pausa {F(seg.Start - prev.End)}s {F(prev.End)}–{F(seg.Start)}⟩");
            sb.Append('[').Append(Time(seg.Start)).Append(" → ").Append(Time(seg.End)).Append("] ");
            var first = true;
            foreach (var w in words.Where(w => w.Start >= seg.Start - 1e-9 && w.End <= seg.End + 1e-9))
            {
                if (!first && prev is not null)
                {
                    var gap = w.Start - prev.End;
                    sb.Append(gap >= pauseThreshold
                        ? $" ⟨pausa {F(gap)}s {F(prev.End)}–{F(w.Start)}⟩ "
                        : " ");
                }
                var bare = w.Text.Trim(',', '.', '?', '!', ';', ':', '…');
                if (Fillers.Contains(bare)) sb.Append($"«{w.Text}@{F(w.Start)}–{F(w.End)}»");
                else if (w.P < lowConfidence) sb.Append($"{w.Text}(?{F(w.P)})");
                else sb.Append(w.Text);
                prev = w;
                first = false;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Una parola per riga: inizio, fine, confidenza, testo.</summary>
    public static string FormatWords(IReadOnlyList<Word> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words) sb.AppendLine($"{F(w.Start),9} {F(w.End),9} {F(w.P)} {w.Text}");
        return sb.ToString();
    }

    public static string ToSrt(IReadOnlyList<Word> words, int maxChars = 42)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var seg in BuildSegments(words, maxWords: 12))
        {
            if (seg.Text.Length == 0) continue;
            sb.AppendLine((++n).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{SrtTime(seg.Start)} --> {SrtTime(seg.End)}");
            sb.AppendLine(Wrap(seg.Text, maxChars));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Cerca una frase (senza distinzione di maiuscole e punteggiatura) e ne restituisce gli intervalli.</summary>
    public static List<(double Start, double End, string Text)> Find(IReadOnlyList<Word> words, string phrase)
    {
        var target = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(t => t.Length > 0).ToArray();
        var result = new List<(double, double, string)>();
        if (target.Length == 0) return result;
        var norm = words.Select(w => Normalize(w.Text)).ToArray();
        for (var i = 0; i + target.Length <= words.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < target.Length && ok; j++) ok = norm[i + j] == target[j];
            if (ok) result.Add((words[i].Start, words[i + target.Length - 1].End,
                string.Join(' ', words.Skip(i).Take(target.Length).Select(w => w.Text))));
        }
        return result;
    }

    /// <summary>Intercalari trovati, con l'intervallo da tagliare (fino all'inizio della parola successiva, se vicina).</summary>
    public static List<(double Start, double End, string Text)> FindFillers(IReadOnlyList<Word> words)
    {
        var result = new List<(double, double, string)>();
        for (var i = 0; i < words.Count; i++)
        {
            var bare = Normalize(words[i].Text);
            if (!Fillers.Contains(bare)) continue;
            var end = i + 1 < words.Count && words[i + 1].Start - words[i].End < 0.3 ? words[i + 1].Start : words[i].End;
            result.Add((words[i].Start, end, words[i].Text));
        }
        return result;
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == '\'').ToArray());

    private static string Wrap(string text, int max)
    {
        if (text.Length <= max) return text;
        var mid = text.Length / 2;
        var split = text.LastIndexOf(' ', mid);
        if (split < 0) split = text.IndexOf(' ', mid);
        return split < 0 ? text : text[..split] + "\n" + text[(split + 1)..];
    }

    /// <summary>Tempo leggibile: m:ss.cc oppure h:mm:ss.cc.</summary>
    public static string Time(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var cs = ts.Milliseconds / 10;
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{cs:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{cs:00}";
    }

    private static string SrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    private static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
