using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Transcription;

/// <summary>Un sottotitolo: intervallo sulla timeline e testo (righe separate da \n).</summary>
public sealed record SubtitleCue(double Start, double End, string Text);

public sealed class SubtitleOptions
{
    /// <summary>Caratteri massimi per riga (42 è lo standard broadcast).</summary>
    public int MaxChars { get; set; } = 42;

    public int MaxLines { get; set; } = 2;

    /// <summary>Durata massima di un sottotitolo, in secondi.</summary>
    public double MaxDuration { get; set; } = 6;

    /// <summary>Una pausa più lunga di così chiude il sottotitolo.</summary>
    public double BreakOnPause { get; set; } = 0.8;

    /// <summary>Quanto il sottotitolo resta a schermo dopo l'ultima parola.</summary>
    public double Hold { get; set; } = 0.4;

    public double MinDuration { get; set; } = 0.9;

    /// <summary>Esclude gli intercalari (ehm, uhm...) dal testo dei sottotitoli.</summary>
    public bool SkipFillers { get; set; } = true;
}

/// <summary>Trasforma le parole della trascrizione (tempi di timeline) in sottotitoli leggibili.</summary>
public static class SubtitleBuilder
{
    public static List<SubtitleCue> Build(IReadOnlyList<Word> words, SubtitleOptions? options = null)
    {
        var o = options ?? new SubtitleOptions();
        var capacity = o.MaxChars * o.MaxLines;
        var groups = new List<List<Word>>();
        var cur = new List<Word>();

        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)))
        {
            if (o.SkipFillers && TranscriptTools.Fillers.Contains(w.Text.Trim(',', '.', '?', '!', ';', ':', '…'))) continue;
            if (cur.Count > 0)
            {
                var len = Length(cur) + 1 + w.Text.Length;
                var gap = w.Start - cur[^1].End;
                var dur = w.End - cur[0].Start;
                if (gap > o.BreakOnPause || dur > o.MaxDuration || len > capacity || !Fits(cur.Append(w).ToList(), o))
                {
                    groups.Add(cur);
                    cur = [];
                }
            }
            cur.Add(w);
            var text = w.Text;
            // Fine frase: si chiude; virgola: si chiude se il sottotitolo è già abbastanza pieno.
            if (text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!') || text.EndsWith('…') ||
                (text.EndsWith(',') || text.EndsWith(';') || text.EndsWith(':')) && Length(cur) > capacity * 0.6)
            {
                groups.Add(cur);
                cur = [];
            }
        }
        if (cur.Count > 0) groups.Add(cur);

        var cues = new List<SubtitleCue>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var start = g[0].Start;
            var end = g[^1].End + o.Hold;
            if (end - start < o.MinDuration) end = start + o.MinDuration;
            if (i + 1 < groups.Count) end = Math.Min(end, groups[i + 1][0].Start - 0.04);
            end = Math.Max(end, g[^1].End);
            cues.Add(new SubtitleCue(Math.Round(start, 3), Math.Round(end, 3), Wrap(g.Select(w => w.Text).ToList(), o)));
        }
        return cues;
    }

    /// <summary>Divide il testo in righe, bilanciando le lunghezze (due righe simili si leggono meglio).</summary>
    internal static string Wrap(List<string> tokens, SubtitleOptions o)
    {
        var text = string.Join(' ', tokens);
        if (text.Length <= o.MaxChars || o.MaxLines <= 1) return text;
        if (o.MaxLines == 2)
        {
            var best = -1;
            var bestScore = int.MaxValue;
            for (var i = 1; i < tokens.Count; i++)
            {
                var a = string.Join(' ', tokens.Take(i)).Length;
                var b = string.Join(' ', tokens.Skip(i)).Length;
                if (a > o.MaxChars || b > o.MaxChars) continue;
                var score = Math.Abs(a - b);
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best > 0) return string.Join(' ', tokens.Take(best)) + "\n" + string.Join(' ', tokens.Skip(best));
        }
        // Più di due righe (o nessuna divisione bilanciata): a capo quando la riga è piena.
        var sb = new StringBuilder();
        var line = 0;
        foreach (var t in tokens)
        {
            if (line > 0 && line + 1 + t.Length > o.MaxChars) { sb.Append('\n'); line = 0; }
            else if (line > 0) { sb.Append(' '); line++; }
            sb.Append(t);
            line += t.Length;
        }
        return sb.ToString();
    }

    private static bool Fits(List<Word> words, SubtitleOptions o) =>
        Wrap(words.Select(w => w.Text).ToList(), o).Split('\n').Length <= o.MaxLines;

    private static int Length(List<Word> words) => words.Sum(w => w.Text.Length) + Math.Max(0, words.Count - 1);

    /// <summary>File SRT dai sottotitoli.</summary>
    public static string ToSrt(IEnumerable<SubtitleCue> cues)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var c in cues.OrderBy(c => c.Start))
        {
            sb.Append((++n).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append(SrtTime(c.Start)).Append(" --> ").Append(SrtTime(c.End)).Append("\r\n");
            sb.Append(c.Text.Replace("\r", "").Replace("\n", "\r\n")).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string SrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }
}
