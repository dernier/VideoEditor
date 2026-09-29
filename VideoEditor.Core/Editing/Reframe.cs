using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Editing;

/// <summary>Rapporto d'aspetto predefinito (come in CapCut).</summary>
public sealed record AspectPreset(string Id, string Label, double Ratio);

/// <summary>
/// Formato di uscita e reinquadratura. La "cornice" è il rettangolo del video sorgente che finisce
/// in uscita, in coordinate normalizzate sul video: centro <c>Fx</c>,<c>Fy</c> (0-1) e larghezza
/// <c>Fw</c> (frazione della larghezza del video; l'altezza segue dal rapporto d'uscita).
/// Una cornice dentro il video lo ritaglia (riempie l'uscita); più grande del video lo rimpicciolisce
/// (bande nere). La cornice si traduce nei parametri già esistenti della clip: Scale, X, Y.
/// </summary>
public static class Reframe
{
    public static readonly AspectPreset[] Presets =
    [
        new("16:9", "16:9 (YouTube, TV)", 16.0 / 9),
        new("4:3", "4:3", 4.0 / 3),
        new("2.35:1", "2.35:1 (cinema)", 2.35),
        new("2:1", "2:1", 2.0),
        new("1.85:1", "1.85:1", 1.85),
        new("9:16", "9:16 (TikTok, Reels, Shorts)", 9.0 / 16),
        new("3:4", "3:4", 3.0 / 4),
        new("5.8in", "5,8\" (telefono verticale)", 1125.0 / 2436),
        new("1:1", "1:1 (quadrato)", 1.0),
    ];

    /// <summary>Interpreta "16:9", "2.35:1", "9x16", "1.777", "5.8in" o un preset.</summary>
    public static double ParseRatio(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(',', '.');
        var preset = Presets.FirstOrDefault(p => p.Id == t || p.Id.Replace(":1", "") == t);
        if (preset is not null) return preset.Ratio;
        if (t is "5.8" or "5.8\"" or "5,8") return 1125.0 / 2436;
        var parts = t.Split(':', 'x', '/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a > 0 && b > 0)
            return a / b;
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && r > 0) return r;
        throw new EditException($"Rapporto non valido: '{text}'. Esempi: {string.Join(", ", Presets.Select(p => p.Id))}.");
    }

    /// <summary>Dimensioni di uscita per un rapporto, mantenendo il lato corto (numeri pari).</summary>
    public static (int Width, int Height) SizeFor(double ratio, int shortSide)
    {
        shortSide = Math.Max(2, shortSide - shortSide % 2);
        int w, h;
        if (ratio >= 1) { h = shortSide; w = (int)Math.Round(shortSide * ratio); }
        else { w = shortSide; h = (int)Math.Round(shortSide / ratio); }
        return (w - w % 2, h - h % 2);
    }

    /// <summary>Cornice → parametri della clip (Scale, X, Y) per un'uscita W×H e un video vw×vh.</summary>
    public static (double Scale, double X, double Y) ToTransform(double fx, double fy, double fw, int outW, int outH, double vw, double vh)
    {
        var videoAspect = vw / vh;
        var framePx = Math.Max(1e-6, fw * vw);   // larghezza della cornice in pixel del video
        var k = outW / framePx;                  // pixel di uscita per pixel del video
        var dw = vw * k;                         // larghezza del video mostrato in uscita
        var scale = dw / Math.Min(outW, outH * videoAspect);
        var x = 0.5 + (0.5 - fx) * vw * k / outW;
        var y = 0.5 + (0.5 - fy) * vh * k / outH;
        return (scale, x, y);
    }

    /// <summary>Parametri della clip → cornice (inversa di <see cref="ToTransform"/>).</summary>
    public static (double Fx, double Fy, double Fw) FromTransform(double scale, double x, double y, int outW, int outH, double vw, double vh)
    {
        var videoAspect = vw / vh;
        var dw = scale * Math.Min(outW, outH * videoAspect);
        var k = dw / vw;
        var fw = outW / k / vw;
        var fx = 0.5 - (x - 0.5) * outW / (vw * k);
        var fy = 0.5 - (y - 0.5) * outH / (vh * k);
        return (fx, fy, fw);
    }

    /// <summary>Cornice che riempie l'uscita tagliando il minimo (la più grande che sta dentro il video, centrata).</summary>
    public static double FillWidth(int outW, int outH, double vw, double vh)
    {
        var outAspect = (double)outW / outH;
        return outAspect <= vw / vh ? vh * outAspect / vw : 1;
    }

    /// <summary>Cornice che contiene tutto il video (bande nere dove serve).</summary>
    public static double FitWidth(int outW, int outH, double vw, double vh)
    {
        var outAspect = (double)outW / outH;
        return outAspect >= vw / vh ? vh * outAspect / vw : 1;
    }
}
