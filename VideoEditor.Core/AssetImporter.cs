using System;
using System.IO;
using System.Linq;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;

namespace VideoEditor.Core;

/// <summary>Importa file nel progetto: analisi con ffprobe ed eventuale copia nella cartella media.</summary>
public static class AssetImporter
{
    public static Asset Import(ProjectStore store, Project project, string file, bool copy = false)
    {
        var full = Path.GetFullPath(file);
        if (!File.Exists(full)) throw new FileNotFoundException($"File non trovato: {file}");
        var existing = project.Assets.FirstOrDefault(a =>
            string.Equals(store.ResolvePath(a.Path), full, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        if (copy && !full.StartsWith(store.Directory, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(store.MediaDir);
            var dest = Path.Combine(store.MediaDir, Path.GetFileName(full));
            for (var i = 2; File.Exists(dest); i++)
                dest = Path.Combine(store.MediaDir, $"{Path.GetFileNameWithoutExtension(full)}-{i}{Path.GetExtension(full)}");
            File.Copy(full, dest);
            full = dest;
        }

        var asset = MediaProbe.Probe(full);
        asset.Path = store.ToProjectPath(full);
        return new TimelineEditor(project).AddAsset(asset);
    }

    /// <summary>
    /// Se la timeline è ancora vuota, il progetto prende risoluzione e fotogrammi al secondo
    /// dal primo video importato (come fanno i programmi di montaggio).
    /// </summary>
    public static bool AdoptFormatIfEmpty(Project project, Asset asset)
    {
        if (asset.Kind != AssetKind.Video || asset.Width <= 0 || asset.Height <= 0) return false;
        if (project.Tracks.Any(t => t.Clips.Count > 0)) return false;
        project.Settings.Width = asset.Width - asset.Width % 2;
        project.Settings.Height = asset.Height - asset.Height % 2;
        if (asset.Fps is > 1 and <= 240) project.Settings.Fps = asset.Fps;
        return true;
    }

    /// <summary>Traccia predefinita per un asset: la prima video libera o la prima audio.</summary>
    public static Track DefaultTrackFor(Project project, Asset asset) =>
        (asset.Kind == AssetKind.Audio
            ? project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio && !t.Locked)
            : project.Tracks.LastOrDefault(t => t.Kind == TrackKind.Video && !t.Locked))
        ?? new TimelineEditor(project).AddTrack(asset.Kind == AssetKind.Audio ? TrackKind.Audio : TrackKind.Video);
}
