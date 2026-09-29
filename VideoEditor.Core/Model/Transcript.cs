namespace VideoEditor.Core.Model;

/// <summary>
/// Trascrizione di un asset con i tempi di ogni parola, nel tempo del file sorgente.
/// Salvata in <c>transcripts/&lt;assetId&gt;.json</c>.
/// </summary>
public sealed class Transcript
{
    public string AssetId { get; set; } = "";
    public string Language { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Word> Words { get; set; } = [];
    public List<Segment> Segments { get; set; } = [];
}

public sealed class Word
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>Confidenza del riconoscimento (0-1).</summary>
    public double P { get; set; } = 1;
}

/// <summary>Frase: gruppo di parole separato da punteggiatura o pause.</summary>
public sealed class Segment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Parola riportata sulla timeline (dove si trova nel montaggio corrente).</summary>
public sealed record TimelineWord(string Text, double Start, double End, double P, string ClipId, string AssetId);
