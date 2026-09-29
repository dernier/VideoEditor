using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Editing;

/// <summary>
/// Operazioni di montaggio sul modello. Usate sia dall'editor sia dalla riga di comando,
/// così le regole (divisione, eliminazione con ricompattamento, ecc.) sono le stesse.
/// </summary>
public sealed class TimelineEditor(Project project)
{
    /// <summary>Durata minima di una clip dopo tagli e divisioni.</summary>
    public const double MinDuration = 0.02;

    public Project Project { get; } = project;

    // ---------- Asset ----------

    public Asset AddAsset(Asset asset)
    {
        if (string.IsNullOrEmpty(asset.Id)) asset.Id = NextId("m", Project.Assets.Select(a => a.Id));
        if (Project.FindAsset(asset.Id) is not null) throw new EditException($"L'asset {asset.Id} esiste già.");
        Project.Assets.Add(asset);
        return asset;
    }

    public void RemoveAsset(string assetId)
    {
        var asset = Project.FindAsset(assetId) ?? throw new EditException($"Asset {assetId} non trovato.");
        var used = Project.Tracks.SelectMany(t => t.Clips).Count(c => c.AssetId == assetId);
        if (used > 0) throw new EditException($"L'asset {assetId} è usato da {used} clip: rimuovile prima.");
        Project.Assets.Remove(asset);
    }

    // ---------- Tracce ----------

    public Track AddTrack(TrackKind kind, string? name = null, int? index = null)
    {
        var prefix = kind switch { TrackKind.Video => "V", TrackKind.Audio => "A", _ => "T" };
        var track = new Track
        {
            Id = NextId(prefix, Project.Tracks.Select(t => t.Id)),
            Kind = kind,
        };
        track.Name = name ?? track.Id;
        if (index is null)
        {
            // Le nuove tracce visive vanno sopra le altre visive, quelle audio in fondo.
            index = kind == TrackKind.Audio ? Project.Tracks.Count : 0;
        }
        Project.Tracks.Insert(Math.Clamp(index.Value, 0, Project.Tracks.Count), track);
        return track;
    }

    public void RemoveTrack(string trackId)
    {
        var t = GetTrack(trackId);
        Project.Tracks.Remove(t);
    }

    public void MoveTrack(string trackId, int index)
    {
        var t = GetTrack(trackId);
        Project.Tracks.Remove(t);
        Project.Tracks.Insert(Math.Clamp(index, 0, Project.Tracks.Count), t);
    }

    public Track SetTrack(string trackId, JsonObject patch)
    {
        var t = GetTrack(trackId);
        patch.Remove("clips");
        patch.Remove("id");
        var updated = Json.Merge(t, patch);
        t.Name = updated.Name;
        t.Muted = updated.Muted;
        t.Hidden = updated.Hidden;
        t.Locked = updated.Locked;
        t.Volume = Math.Max(0, updated.Volume);
        t.Duck = updated.Duck;
        return t;
    }

    // ---------- Clip ----------

    /// <summary>
    /// Aggiunge una clip di un asset. Senza <paramref name="start"/> la accoda alla fine della traccia.
    /// </summary>
    public Clip AddClip(string trackId, string assetId, double? start = null, double inPoint = 0, double? duration = null)
    {
        var track = GetTrack(trackId);
        EnsureUnlocked(track);
        var asset = Project.FindAsset(assetId) ?? throw new EditException($"Asset {assetId} non trovato.");
        if (track.Kind == TrackKind.Text) throw new EditException("Sulle tracce di testo si aggiungono solo testi (vedit text add).");
        if (track.Kind == TrackKind.Audio && !asset.HasAudio) throw new EditException($"L'asset {assetId} non ha audio.");
        if (track.Kind == TrackKind.Video && asset.Kind == AssetKind.Audio) throw new EditException("Un file audio va su una traccia audio.");

        var isStill = asset.Kind == AssetKind.Image;
        var available = isStill ? double.MaxValue : Math.Max(0, asset.Duration - inPoint);
        if (!isStill && inPoint >= asset.Duration) throw new EditException($"Il punto di ingresso {inPoint:0.###} è oltre la durata dell'asset ({asset.Duration:0.###}).");
        var dur = duration ?? (isStill ? 5 : available);
        dur = Math.Min(dur, available);
        if (dur < MinDuration) throw new EditException("Durata della clip troppo breve.");

        var clip = new Clip
        {
            Id = NextClipId(),
            AssetId = assetId,
            Start = Math.Max(0, start ?? TrackEnd(track)),
            In = Math.Max(0, inPoint),
            Duration = dur,
        };
        Insert(track, clip);
        return clip;
    }

    public Clip AddText(string? trackId, string text, double start, double duration, TextStyle? style = null)
    {
        var track = trackId is null
            ? Project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Text) ?? AddTrack(TrackKind.Text, "Testi")
            : GetTrack(trackId);
        EnsureUnlocked(track);
        if (track.Kind != TrackKind.Text) throw new EditException($"La traccia {track.Id} non è una traccia di testo.");
        if (duration < MinDuration) throw new EditException("Durata del testo troppo breve.");
        var clip = new Clip
        {
            Id = NextClipId(),
            Text = text,
            Start = Math.Max(0, start),
            Duration = duration,
            Y = 0.85,
            Style = style ?? new TextStyle(),
        };
        Insert(track, clip);
        return clip;
    }

    public Clip MoveClip(string clipId, double start, string? trackId = null)
    {
        var (track, clip) = GetClip(clipId);
        EnsureUnlocked(track);
        var target = trackId is null ? track : GetTrack(trackId);
        EnsureUnlocked(target);
        if (target != track)
        {
            var asset = clip.AssetId is null ? null : Project.FindAsset(clip.AssetId);
            var allowed = target.Kind switch
            {
                TrackKind.Text => clip.IsText,
                TrackKind.Video => !clip.IsText && asset?.Kind != AssetKind.Audio,
                _ => !clip.IsText && asset?.HasAudio == true,
            };
            if (!allowed) throw new EditException($"Non si può spostare la clip {clipId} su una traccia di tipo {target.Kind}.");
            track.Clips.Remove(clip);
            clip.Start = Math.Max(0, start);
            Insert(target, clip);
        }
        else
        {
            clip.Start = Math.Max(0, start);
            Sort(track);
        }
        return clip;
    }

    /// <summary>
    /// Rifila i bordi di una clip: <paramref name="newStart"/> sposta l'inizio (e il punto di ingresso),
    /// <paramref name="newEnd"/> sposta la fine. I tempi sono sulla timeline.
    /// </summary>
    public Clip TrimClip(string clipId, double? newStart, double? newEnd)
    {
        var (track, clip) = GetClip(clipId);
        EnsureUnlocked(track);
        var asset = clip.AssetId is null ? null : Project.FindAsset(clip.AssetId);
        var isStill = clip.IsText || asset?.Kind == AssetKind.Image;
        var end = clip.End;
        if (newStart is double s)
        {
            var delta = s - clip.Start;
            var newIn = clip.In + delta;
            if (!isStill && newIn < 0) { delta -= newIn; newIn = 0; }
            if (clip.Start + delta < 0) { var fix = -(clip.Start + delta); delta += fix; newIn += fix; }
            if (clip.Duration - delta < MinDuration) throw new EditException("La clip diventerebbe troppo corta.");
            clip.Start += delta;
            clip.In = isStill ? 0 : newIn;
            clip.Duration = end - clip.Start;
        }
        if (newEnd is double e)
        {
            var dur = e - clip.Start;
            if (!isStill && asset is not null) dur = Math.Min(dur, asset.Duration - clip.In);
            if (dur < MinDuration) throw new EditException("La clip diventerebbe troppo corta.");
            clip.Duration = dur;
        }
        Sort(track);
        return clip;
    }

    /// <summary>Modifica le proprietà di una clip con una patch JSON (volume, x, y, text, style...).</summary>
    public Clip SetClip(string clipId, JsonObject patch)
    {
        var (track, clip) = GetClip(clipId);
        EnsureUnlocked(track);
        patch.Remove("id");
        var u = Json.Merge(clip, patch);
        if (u.AssetId != clip.AssetId && u.AssetId is not null && Project.FindAsset(u.AssetId) is null)
            throw new EditException($"Asset {u.AssetId} non trovato.");
        if (u.Duration < MinDuration) throw new EditException("Durata troppo breve.");
        clip.AssetId = u.AssetId;
        clip.Start = Math.Max(0, u.Start);
        clip.In = Math.Max(0, u.In);
        clip.Duration = u.Duration;
        clip.Volume = Math.Max(0, u.Volume);
        clip.FadeIn = Math.Clamp(u.FadeIn, 0, clip.Duration);
        clip.FadeOut = Math.Clamp(u.FadeOut, 0, clip.Duration);
        clip.X = u.X;
        clip.Y = u.Y;
        clip.Scale = Math.Max(0.01, u.Scale);
        clip.Opacity = Math.Clamp(u.Opacity, 0, 1);
        clip.Text = u.Text;
        clip.Style = u.Style;
        clip.Note = u.Note;
        Sort(track);
        return clip;
    }

    /// <summary>Divide una clip nel punto <paramref name="time"/> della timeline. Restituisce la seconda metà.</summary>
    public Clip SplitClip(string clipId, double time)
    {
        var (track, clip) = GetClip(clipId);
        EnsureUnlocked(track);
        if (time <= clip.Start + MinDuration || time >= clip.End - MinDuration)
            throw new EditException($"Il punto {time:0.###} non cade all'interno della clip {clipId}.");
        var right = Clone(clip);
        right.Id = NextClipId();
        var offset = time - clip.Start;
        var isStill = clip.IsText || (clip.AssetId is not null && Project.FindAsset(clip.AssetId)?.Kind == AssetKind.Image);
        right.Start = time;
        right.In = isStill ? clip.In : clip.In + offset;
        right.Duration = clip.Duration - offset;
        right.FadeIn = 0;
        clip.Duration = offset;
        clip.FadeOut = 0;
        Insert(track, right);
        return right;
    }

    /// <summary>Divide tutte le clip che attraversano <paramref name="time"/> (sulle tracce indicate o su tutte).</summary>
    public List<Clip> SplitAt(double time, IEnumerable<string>? trackIds = null)
    {
        var ids = trackIds?.ToHashSet();
        var created = new List<Clip>();
        foreach (var track in Project.Tracks.Where(t => (ids is null || ids.Contains(t.Id)) && !t.Locked).ToList())
        {
            foreach (var clip in track.Clips.Where(c => time > c.Start + MinDuration && time < c.End - MinDuration).ToList())
                created.Add(SplitClip(clip.Id, time));
        }
        return created;
    }

    /// <summary>Rimuove una clip; con <paramref name="ripple"/> le clip successive della traccia scorrono indietro.</summary>
    public void RemoveClip(string clipId, bool ripple = false)
    {
        var (track, clip) = GetClip(clipId);
        EnsureUnlocked(track);
        track.Clips.Remove(clip);
        if (ripple)
            foreach (var c in track.Clips.Where(c => c.Start >= clip.End - 1e-9)) c.Start -= clip.Duration;
    }

    /// <summary>
    /// Elimina l'intervallo [from, to) dalla timeline e ricompatta: le clip a cavallo vengono tagliate,
    /// quelle successive scorrono indietro. Le tracce bloccate non vengono toccate.
    /// </summary>
    public void RippleDelete(double from, double to, IEnumerable<string>? trackIds = null)
    {
        if (to <= from) throw new EditException("Intervallo non valido: la fine deve essere dopo l'inizio.");
        var len = to - from;
        var ids = trackIds?.ToHashSet();
        foreach (var track in Project.Tracks.Where(t => (ids is null || ids.Contains(t.Id)) && !t.Locked))
        {
            var result = new List<Clip>();
            foreach (var c in track.Clips)
            {
                if (c.End <= from + 1e-9) { result.Add(c); continue; }
                if (c.Start >= to - 1e-9) { c.Start -= len; result.Add(c); continue; }
                var isStill = c.IsText || (c.AssetId is not null && Project.FindAsset(c.AssetId)?.Kind == AssetKind.Image);
                var keepLeft = from - c.Start;
                var keepRight = c.End - to;
                if (keepLeft >= MinDuration && keepRight >= MinDuration)
                {
                    // L'intervallo cade dentro la clip: la parte destra diventa una nuova clip.
                    var right = Clone(c);
                    right.Id = NextClipId(extra: result.Select(x => x.Id));
                    right.In = isStill ? c.In : c.In + (to - c.Start);
                    right.Start = from;
                    right.Duration = keepRight;
                    right.FadeIn = 0;
                    c.Duration = keepLeft;
                    c.FadeOut = 0;
                    result.Add(c);
                    result.Add(right);
                }
                else if (keepLeft >= MinDuration)
                {
                    c.Duration = keepLeft;
                    c.FadeOut = Math.Min(c.FadeOut, c.Duration);
                    result.Add(c);
                }
                else if (keepRight >= MinDuration)
                {
                    if (!isStill) c.In += to - c.Start;
                    c.Start = from;
                    c.Duration = keepRight;
                    c.FadeIn = Math.Min(c.FadeIn, c.Duration);
                    result.Add(c);
                }
                // altrimenti la clip è interamente nell'intervallo e viene eliminata
            }
            track.Clips = result;
            Sort(track);
        }
        if (ids is null)
        {
            Project.Markers.RemoveAll(m => m.Time >= from && m.Time < to);
            foreach (var m in Project.Markers.Where(m => m.Time >= to)) m.Time -= len;
        }
    }

    /// <summary>Elimina più intervalli (ad esempio le pause trovate da Claude), dal più lontano al più vicino.</summary>
    public void RippleDeleteRanges(IEnumerable<(double From, double To)> ranges, IEnumerable<string>? trackIds = null)
    {
        var ids = trackIds?.ToList();
        foreach (var (from, to) in MergeRanges(ranges).OrderByDescending(r => r.From))
            RippleDelete(from, to, ids);
    }

    /// <summary>Unisce intervalli sovrapposti o adiacenti.</summary>
    public static List<(double From, double To)> MergeRanges(IEnumerable<(double From, double To)> ranges)
    {
        var merged = new List<(double From, double To)>();
        foreach (var r in ranges.Where(r => r.To > r.From).OrderBy(r => r.From))
        {
            if (merged.Count > 0 && r.From <= merged[^1].To + 1e-6)
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, r.To));
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>Inserisce uno spazio vuoto di <paramref name="length"/> secondi in <paramref name="at"/>.</summary>
    public void InsertGap(double at, double length)
    {
        if (length <= 0) throw new EditException("La lunghezza dello spazio deve essere positiva.");
        SplitAt(at);
        foreach (var track in Project.Tracks.Where(t => !t.Locked))
            foreach (var c in track.Clips.Where(c => c.Start >= at - 1e-9)) c.Start += length;
        foreach (var m in Project.Markers.Where(m => m.Time >= at)) m.Time += length;
    }

    // ---------- Marcatori ----------

    public Marker AddMarker(double time, string label, string? color = null)
    {
        var m = new Marker { Id = NextId("k", Project.Markers.Select(x => x.Id)), Time = Math.Max(0, time), Label = label, Color = color };
        Project.Markers.Add(m);
        Project.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
        return m;
    }

    public void RemoveMarker(string id)
    {
        if (Project.Markers.RemoveAll(m => m.Id == id) == 0) throw new EditException($"Marcatore {id} non trovato.");
    }

    // ---------- Validazione ----------

    /// <summary>Controlla il progetto e restituisce errori e avvisi leggibili.</summary>
    public List<string> Validate()
    {
        var issues = new List<string>();
        var clipIds = new HashSet<string>();
        foreach (var t in Project.Tracks)
        {
            Clip? prev = null;
            foreach (var c in t.Clips.OrderBy(c => c.Start))
            {
                if (!clipIds.Add(c.Id)) issues.Add($"ERRORE: id clip duplicato {c.Id}.");
                if (c.Duration < MinDuration) issues.Add($"ERRORE: clip {c.Id} con durata {c.Duration:0.###}.");
                if (c.Start < 0) issues.Add($"ERRORE: clip {c.Id} inizia prima di zero.");
                if (t.Kind == TrackKind.Text && !c.IsText) issues.Add($"ERRORE: la traccia di testo {t.Id} contiene la clip non testuale {c.Id}.");
                if (t.Kind != TrackKind.Text && c.IsText) issues.Add($"ERRORE: testo {c.Id} su traccia non di testo {t.Id}.");
                if (!c.IsText)
                {
                    var a = c.AssetId is null ? null : Project.FindAsset(c.AssetId);
                    if (a is null) issues.Add($"ERRORE: clip {c.Id} senza asset valido ({c.AssetId}).");
                    else if (a.Kind != AssetKind.Image && c.In + c.Duration > a.Duration + 0.05)
                        issues.Add($"AVVISO: clip {c.Id} supera la fine del file sorgente ({c.In + c.Duration:0.###} > {a.Duration:0.###}).");
                }
                if (prev is not null && c.Start < prev.End - 1e-3)
                    issues.Add($"AVVISO: sulla traccia {t.Id} le clip {prev.Id} e {c.Id} si sovrappongono ({c.Start:0.###} < {prev.End:0.###}).");
                prev = c;
            }
        }
        return issues;
    }

    // ---------- Supporto ----------

    public Track GetTrack(string id) => Project.FindTrack(id) ?? throw new EditException($"Traccia {id} non trovata.");

    public (Track Track, Clip Clip) GetClip(string id) =>
        Project.FindClip(id) ?? throw new EditException($"Clip {id} non trovata.");

    /// <summary>Clip visibili sulla traccia nell'istante indicato.</summary>
    public static Clip? ClipAt(Track track, double time) =>
        track.Clips.LastOrDefault(c => time >= c.Start && time < c.End);

    private static double TrackEnd(Track track) => track.Clips.Select(c => c.End).DefaultIfEmpty(0).Max();

    private static void EnsureUnlocked(Track t)
    {
        if (t.Locked) throw new EditException($"La traccia {t.Id} è bloccata.");
    }

    private static void Insert(Track track, Clip clip)
    {
        track.Clips.Add(clip);
        Sort(track);
    }

    private static void Sort(Track track) => track.Clips.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static Clip Clone(Clip c) => Json.Deserialize<Clip>(Json.Serialize(c));

    private string NextClipId(IEnumerable<string>? extra = null) =>
        NextId("c", Project.Tracks.SelectMany(t => t.Clips).Select(c => c.Id).Concat(extra ?? []));

    internal static string NextId(string prefix, IEnumerable<string> existing)
    {
        var max = 0;
        foreach (var id in existing)
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(prefix.Length), out var n))
                max = Math.Max(max, n);
        }
        return prefix + (max + 1);
    }
}

public sealed class EditException(string message) : Exception(message);
