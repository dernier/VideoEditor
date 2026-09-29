using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoEditor.Core.Model;

/// <summary>
/// Progetto di montaggio: è l'unica fonte di verità condivisa tra l'editor e Claude
/// (file <c>project.vedit.json</c> nella cartella del progetto). Tutti i tempi sono in secondi.
/// </summary>
public sealed class Project
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Nuovo progetto";
    public ProjectSettings Settings { get; set; } = new();
    public List<Asset> Assets { get; set; } = [];

    /// <summary>
    /// Tracce nell'ordine in cui sono mostrate (dall'alto in basso). Tra le tracce visive
    /// (video e testo) quelle più in alto nella lista sono in primo piano.
    /// </summary>
    public List<Track> Tracks { get; set; } = [];

    public List<Marker> Markers { get; set; } = [];

    /// <summary>Durata della timeline: fine dell'ultima clip su una traccia qualsiasi.</summary>
    public double GetDuration() =>
        Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    public Asset? FindAsset(string id) => Assets.FirstOrDefault(a => a.Id == id);
    public Track? FindTrack(string id) => Tracks.FirstOrDefault(t => t.Id == id);

    public (Track Track, Clip Clip)? FindClip(string id)
    {
        foreach (var t in Tracks)
            foreach (var c in t.Clips)
                if (c.Id == id) return (t, c);
        return null;
    }

    /// <summary>Progetto vuoto con le tracce predefinite (testi, overlay, video, musica, effetti).</summary>
    public static Project CreateDefault(string name) => new()
    {
        Name = name,
        Tracks =
        [
            new Track { Id = "T1", Name = "Testi", Kind = TrackKind.Text },
            new Track { Id = "M1", Name = "Maschere", Kind = TrackKind.Mask },
            new Track { Id = "V2", Name = "Overlay", Kind = TrackKind.Video },
            new Track { Id = "V1", Name = "Video", Kind = TrackKind.Video },
            new Track { Id = "A1", Name = "Musica", Kind = TrackKind.Audio, Duck = true, Volume = 0.5 },
            new Track { Id = "A2", Name = "Effetti", Kind = TrackKind.Audio },
        ],
    };
}

public sealed class ProjectSettings
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public int SampleRate { get; set; } = 48000;
    public string BackgroundColor { get; set; } = "#000000";

    /// <summary>Lingua per la trascrizione (codice ISO, "auto" per il rilevamento).</summary>
    public string Language { get; set; } = "it";

    /// <summary>Font per i testi. Se vuoto si usa un font di sistema (Arial su Windows).</summary>
    public string? FontFile { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AssetKind>))]
public enum AssetKind { Video, Audio, Image }

/// <summary>File multimediale importato nel progetto.</summary>
public sealed class Asset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Percorso relativo alla cartella del progetto, oppure assoluto se il file è altrove.</summary>
    public string Path { get; set; } = "";

    public AssetKind Kind { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }

    /// <summary>Il video ha un canale alfa (overlay trasparenti, es. ProRes 4444 o WebM VP9).</summary>
    public bool HasAlpha { get; set; }

    public string? Codec { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind { Video, Audio, Text, Mask }

public sealed class Track
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TrackKind Kind { get; set; }
    public bool Muted { get; set; }
    public bool Hidden { get; set; }
    public bool Locked { get; set; }
    public double Volume { get; set; } = 1;

    /// <summary>Abbassa automaticamente il volume quando c'è parlato sulle altre tracce (ducking).</summary>
    public bool Duck { get; set; }

    public List<Clip> Clips { get; set; } = [];

    [JsonIgnore] public bool IsVisual => Kind is TrackKind.Video or TrackKind.Text or TrackKind.Mask;
}

/// <summary>
/// Clip sulla timeline. Le clip multimediali puntano a un <see cref="Asset"/> (da <see cref="In"/>
/// per <see cref="Duration"/> secondi); le clip di testo usano <see cref="Text"/> e <see cref="Style"/>.
/// </summary>
public sealed class Clip
{
    public string Id { get; set; } = "";
    public string? AssetId { get; set; }

    /// <summary>Posizione sulla timeline.</summary>
    public double Start { get; set; }

    /// <summary>Punto di ingresso nel file sorgente.</summary>
    public double In { get; set; }

    public double Duration { get; set; }

    public double Volume { get; set; } = 1;
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }

    /// <summary>Centro orizzontale (0 = bordo sinistro, 1 = bordo destro).</summary>
    public double X { get; set; } = 0.5;

    /// <summary>Centro verticale (0 = alto, 1 = basso).</summary>
    public double Y { get; set; } = 0.5;

    /// <summary>1 = adattata al fotogramma; 0.3 = riquadro piccolo (picture in picture).</summary>
    public double Scale { get; set; } = 1;

    public double Opacity { get; set; } = 1;

    public string? Text { get; set; }
    public TextStyle? Style { get; set; }

    /// <summary>Maschera di oscuramento (solo sulle tracce Maschere).</summary>
    public MaskSpec? Mask { get; set; }

    /// <summary>Nota libera (es. perché Claude ha fatto questo taglio).</summary>
    public string? Note { get; set; }

    [JsonIgnore] public double End => Start + Duration;
    [JsonIgnore] public bool IsText => Text is not null;
    [JsonIgnore] public bool IsMask => Mask is not null;
}

public sealed class TextStyle
{
    /// <summary>Altezza del carattere in pixel riferita alla risoluzione del progetto.</summary>
    public double FontSize { get; set; } = 64;

    public string Color { get; set; } = "#FFFFFF";

    /// <summary>Colore del riquadro di sfondo (es. "#000000AA"); vuoto per nessun riquadro.</summary>
    public string? BoxColor { get; set; }

    public double BoxPadding { get; set; } = 16;
    public string? BorderColor { get; set; } = "#000000";
    public double BorderWidth { get; set; } = 2;
}

public sealed class Marker
{
    public string Id { get; set; } = "";
    public double Time { get; set; }
    public string Label { get; set; } = "";
    public string? Color { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MaskShape>))]
public enum MaskShape { Rect, Ellipse }

[JsonConverter(typeof(JsonStringEnumConverter<MaskEffect>))]
public enum MaskEffect { Blur, Pixelate, Solid }

/// <summary>
/// Zona da oscurare. Coordinate normalizzate sul fotogramma: <see cref="X"/>/<see cref="Y"/> sono il centro,
/// <see cref="W"/>/<see cref="H"/> larghezza e altezza (0-1). Con i <see cref="Keyframes"/> la zona si muove:
/// i tempi dei keyframe sono nel "tempo della maschera" (come il punto di ingresso di un video:
/// istante sulla timeline = Start + (T - In)), così dividere e rifilare non altera il movimento.
/// </summary>
public sealed class MaskSpec
{
    public MaskShape Shape { get; set; } = MaskShape.Ellipse;
    public MaskEffect Effect { get; set; } = MaskEffect.Blur;

    /// <summary>Sfocatura: raggio in pixel; pixel: lato del quadretto in pixel (alla risoluzione del progetto).</summary>
    public double Strength { get; set; } = 30;

    /// <summary>Colore per l'effetto riquadro pieno.</summary>
    public string Color { get; set; } = "#000000";

    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double W { get; set; } = 0.2;
    public double H { get; set; } = 0.25;

    public List<MaskKey> Keyframes { get; set; } = [];

    /// <summary>Rettangolo all'istante <paramref name="maskTime"/> (interpolazione lineare, estremi mantenuti).</summary>
    public MaskKey At(double maskTime)
    {
        if (Keyframes.Count == 0) return new MaskKey { T = maskTime, X = X, Y = Y, W = W, H = H };
        var keys = Keyframes.OrderBy(k => k.T).ToList();
        if (maskTime <= keys[0].T) return keys[0] with { T = maskTime };
        if (maskTime >= keys[^1].T) return keys[^1] with { T = maskTime };
        for (var i = 0; i < keys.Count - 1; i++)
        {
            var a = keys[i];
            var b = keys[i + 1];
            if (maskTime > b.T) continue;
            var f = b.T - a.T < 1e-9 ? 0 : (maskTime - a.T) / (b.T - a.T);
            return new MaskKey
            {
                T = maskTime,
                X = a.X + (b.X - a.X) * f,
                Y = a.Y + (b.Y - a.Y) * f,
                W = a.W + (b.W - a.W) * f,
                H = a.H + (b.H - a.H) * f,
            };
        }
        return keys[^1] with { T = maskTime };
    }
}

public sealed record MaskKey
{
    public double T { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}
