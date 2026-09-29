using VideoEditor.Core.Media;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Editing;

/// <summary>Trova le pause sulla timeline (a partire dall'audio delle clip di una traccia).</summary>
public static class SilenceFinder
{
    /// <summary>
    /// Pause della traccia indicata (di default la prima traccia video con audio), in tempo di timeline.
    /// Ogni pausa viene accorciata di <paramref name="pad"/> secondi per lato, per lasciare un respiro naturale.
    /// </summary>
    public static async Task<(Track Track, List<(double From, double To)> Ranges)> FindAsync(ProjectStore store, Project project,
        string? trackId = null, double noiseDb = -35, double minDuration = 0.6, double pad = 0.15, CancellationToken ct = default)
    {
        var track = trackId is not null
            ? project.FindTrack(trackId) ?? throw new EditException($"Traccia {trackId} non trovata.")
            : project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video && !t.Muted && HasAudioClips(project, t))
              ?? project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio && !t.Duck && !t.Muted && HasAudioClips(project, t))
              ?? throw new EditException("Nessuna traccia con audio da analizzare.");

        var perAsset = new Dictionary<string, List<MediaCache.Silence>>();
        var raw = new List<(double From, double To)>();
        foreach (var clip in track.Clips)
        {
            var asset = clip.AssetId is null ? null : project.FindAsset(clip.AssetId);
            if (asset is not { HasAudio: true }) continue;
            if (!perAsset.TryGetValue(asset.Id, out var silences))
                perAsset[asset.Id] = silences = await MediaCache.DetectSilencesAsync(store, asset, noiseDb, Math.Min(minDuration, 0.3), ct);
            var srcEnd = clip.In + clip.Duration;
            foreach (var s in silences)
            {
                var a = Math.Max(s.Start, clip.In);
                var b = Math.Min(s.End, srcEnd);
                if (b - a <= 0.01) continue;
                raw.Add((clip.Start + (a - clip.In), clip.Start + (b - clip.In)));
            }
        }

        // Pause a cavallo di due clip vengono unite prima di accorciarle.
        var merged = TimelineEditor.MergeRanges(raw.Select(r => (r.From, r.To + 0.01))).Select(r => (r.From, r.To - 0.01));
        var timelineEnd = project.GetDuration();
        var result = new List<(double From, double To)>();
        foreach (var (from, to) in merged)
        {
            if (to - from < minDuration) continue;
            var f = from <= 0.01 ? 0 : from + pad;
            var t = to >= timelineEnd - 0.01 ? timelineEnd : to - pad;
            if (t - f > 0.05) result.Add((Math.Round(f, 3), Math.Round(t, 3)));
        }
        return (track, result);
    }

    private static bool HasAudioClips(Project p, Track t) =>
        t.Clips.Any(c => c.AssetId is not null && p.FindAsset(c.AssetId)?.HasAudio == true);
}
