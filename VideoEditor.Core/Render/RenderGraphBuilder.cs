using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Render;

/// <summary>Cosa renderizzare: un intervallo della timeline, a una certa risoluzione.</summary>
public sealed class RenderRequest
{
    public double From { get; init; }
    public double? To { get; init; }

    /// <summary>Altezza di uscita (la larghezza segue le proporzioni del progetto). Vuoto = risoluzione del progetto.</summary>
    public int? Height { get; init; }

    public bool Video { get; init; } = true;
    public bool Audio { get; init; } = true;

    /// <summary>Sovrappone una griglia al 10% (aiuta a stimare le coordinate nei fotogrammi esportati).</summary>
    public bool Grid { get; init; }
}

/// <summary>Risultato della costruzione: argomenti di input, filtergraph e file di testo di appoggio.</summary>
public sealed class RenderGraph
{
    public List<string> InputArgs { get; } = [];
    public string Filter { get; set; } = "";
    public string? VideoLabel { get; set; }
    public string? AudioLabel { get; set; }
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int InputCount { get; set; }

    /// <summary>File di testo per drawtext (nome relativo alla cartella di lavoro → contenuto).</summary>
    public Dictionary<string, string> TextFiles { get; } = [];
}

/// <summary>
/// Traduce la timeline in un filtergraph ffmpeg: le tracce visive vengono sovrapposte dal basso
/// verso l'alto su uno sfondo, i testi disegnati con drawtext, l'audio mixato con ducking opzionale.
/// Le clip consecutive dello stesso file (tipico dopo i tagli delle pause) sono unite in un solo
/// input per non aprire centinaia di decoder.
/// </summary>
public sealed class RenderGraphBuilder(Project project, Func<string, string> resolvePath, string? fontFile)
{
    private const double Eps = 1e-6;

    private sealed record Piece(Clip Clip, Asset? Asset, double TlStart, double SrcIn, double Dur);

    private sealed class Group
    {
        public required List<Piece> Pieces { get; init; }

        /// <summary>Segmenti nel file sorgente (inizio assoluto, durata), allineati ai fotogrammi se più di uno.</summary>
        public required List<(double Src, double Len)> Segments { get; init; }

        public double TlStart { get; init; }
        public double Duration => Segments.Sum(s => s.Len);
        public int? VideoInput { get; set; }
        public Clip First => Pieces[0].Clip;
        public Clip Last => Pieces[^1].Clip;
        public bool StartsAtClipStart => Math.Abs(Pieces[0].TlStart - (Pieces[0].Clip.Start - _from)) < 1e-4;
        public bool EndsAtClipEnd => Math.Abs(Pieces[^1].TlStart + Pieces[^1].Dur - (Pieces[^1].Clip.End - _from)) < 1e-4;
        internal double _from;
    }

    private readonly List<string> _statements = [];
    private RenderGraph _graph = new();
    private int _label;
    private double _from;
    private double _fps;
    private double _scale;

    public RenderGraph Build(RenderRequest request)
    {
        var s = project.Settings;
        _from = Math.Max(0, request.From);
        var to = request.To ?? project.GetDuration();
        if (to - _from < Eps) throw new InvalidOperationException("Niente da renderizzare: la timeline è vuota o l'intervallo è nullo.");
        var duration = to - _from;
        _fps = s.Fps > 0 ? s.Fps : 30;
        var height = Even(request.Height ?? s.Height);
        var width = Even((int)Math.Round(s.Width * (height / (double)s.Height)));
        _scale = height / (double)s.Height;
        _graph = new RenderGraph { Duration = duration, Width = width, Height = height };
        _statements.Clear();
        _label = 0;
        _nextInput = 0;

        var groupsByTrack = project.Tracks.ToDictionary(t => t.Id, t => BuildGroups(t, _from, to));

        if (request.Video)
        {
            var cur = "base";
            _statements.Add($"color=c={Color(s.BackgroundColor)}:s={width}x{height}:r={F(_fps)}:d={F(duration)},format=yuv420p[base]");
            // Dal basso (fine della lista) verso l'alto: le tracce in cima alla lista sono in primo piano.
            for (var i = project.Tracks.Count - 1; i >= 0; i--)
            {
                var track = project.Tracks[i];
                if (!track.IsVisual || track.Hidden) continue;
                foreach (var g in groupsByTrack[track.Id])
                {
                    if (track.Kind == TrackKind.Text) cur = AddText(cur, g.Pieces[0], width, height);
                    else if (track.Kind == TrackKind.Mask) cur = AddMask(cur, g.Pieces[0], width, height);
                    else if (g.Pieces[0].Asset is { HasVideo: true }) cur = AddVideoLayer(cur, g, width, height);
                }
            }
            _statements.Add(request.Grid
                ? $"[{cur}]drawgrid=w=iw/10:h=ih/10:t=2:c=white@0.7,format=yuv420p[vout]"
                : $"[{cur}]format=yuv420p[vout]");
            _graph.VideoLabel = "vout";
        }

        if (request.Audio)
        {
            var voice = new List<string>();
            var ducked = new List<string>();
            foreach (var track in project.Tracks)
            {
                if (track.Muted || track.Kind == TrackKind.Text || track.Volume <= 0) continue;
                foreach (var g in groupsByTrack[track.Id])
                {
                    if (g.Pieces[0].Asset is not { HasAudio: true }) continue;
                    var label = AddAudio(g, track);
                    if (label is not null) (track.Duck ? ducked : voice).Add(label);
                }
            }
            _graph.AudioLabel = MixAudio(voice, ducked, duration, s.SampleRate);
        }

        _graph.Filter = string.Join(";\n", _statements);
        _graph.InputCount = CountInputs();
        return _graph;
    }

    // ---------- Suddivisione della timeline ----------

    private List<Group> BuildGroups(Track track, double from, double to)
    {
        var pieces = new List<Piece>();
        foreach (var c in track.Clips.OrderBy(c => c.Start))
        {
            var vs = Math.Max(c.Start, from);
            var ve = Math.Min(c.End, to);
            if (ve - vs < Eps) continue;
            var asset = c.AssetId is null ? null : project.FindAsset(c.AssetId);
            if (!c.IsText && !c.IsMask && asset is null) continue;
            var isStill = c.IsText || asset?.Kind == AssetKind.Image;
            pieces.Add(new Piece(c, asset, vs - from, isStill ? 0 : c.In + (vs - c.Start), ve - vs));
        }

        var groups = new List<Group>();
        var run = new List<Piece>();
        void Flush()
        {
            if (run.Count == 0) return;
            groups.Add(MakeGroup(run.ToList(), from));
            run.Clear();
        }
        foreach (var p in pieces)
        {
            if (run.Count > 0 && !CanJoin(run[^1], p)) Flush();
            run.Add(p);
        }
        Flush();
        return groups;
    }

    private static bool CanJoin(Piece a, Piece b)
    {
        if (a.Asset is null || b.Asset is null || a.Asset != b.Asset || a.Asset.Kind == AssetKind.Image) return false;
        if (Math.Abs(b.TlStart - (a.TlStart + a.Dur)) > 1e-3) return false; // devono essere contigue sulla timeline
        if (b.SrcIn < a.SrcIn + a.Dur - 1e-3) return false; // e in avanti nel file sorgente
        var ca = a.Clip;
        var cb = b.Clip;
        return ca.FadeOut == 0 && cb.FadeIn == 0 && ca.Volume == cb.Volume && ca.X == cb.X && ca.Y == cb.Y &&
               ca.Scale == cb.Scale && ca.Opacity == cb.Opacity;
    }

    private Group MakeGroup(List<Piece> pieces, double from)
    {
        List<(double, double)> segments;
        if (pieces.Count == 1)
        {
            segments = [(pieces[0].SrcIn, pieces[0].Dur)];
        }
        else
        {
            // Allinea inizio e fine di ogni segmento alla griglia dei fotogrammi dell'input
            // (che parte dal primo segmento): video e audio restano perfettamente sincronizzati.
            var origin = pieces[0].SrcIn;
            segments = [];
            foreach (var p in pieces)
            {
                var s = Snap(p.SrcIn - origin);
                var e = Snap(p.SrcIn + p.Dur - origin);
                if (e - s > Eps) segments.Add((origin + s, e - s));
            }
            if (segments.Count == 0) segments.Add((pieces[0].SrcIn, pieces[0].Dur));
        }
        return new Group { Pieces = pieces, Segments = segments, TlStart = pieces[0].TlStart, _from = from };
    }

    private double Snap(double t) => Math.Ceiling(t * _fps - 1e-6) / _fps;

    // ---------- Video ----------

    private string AddVideoLayer(string cur, Group g, int width, int height)
    {
        var asset = g.Pieces[0].Asset!;
        var clip = g.First;
        var path = resolvePath(asset.Path);
        string chain;
        int idx;
        if (asset.Kind == AssetKind.Image)
        {
            idx = AddInput(["-loop", "1", "-framerate", F(_fps), "-t", F(g.Duration), "-i", path]);
            chain = $"[{idx}:v]setpts=PTS-STARTPTS,fps={F(_fps)}";
        }
        else if (g.Pieces.Count == 1)
        {
            var (src, len) = g.Segments[0];
            idx = AddInput([.. AlphaDecoder(asset), "-ss", F(src), "-t", F(len), "-i", path]);
            chain = $"[{idx}:v]setpts=PTS-STARTPTS,fps={F(_fps)}";
        }
        else
        {
            var origin = g.Segments[0].Src;
            var span = g.Segments[^1].Src + g.Segments[^1].Len - origin;
            idx = AddInput([.. AlphaDecoder(asset), "-ss", F(origin), "-t", F(span + 1 / _fps), "-i", path]);
            var expr = string.Join("+", g.Segments.Select(sg =>
                $"gte(t,{F(sg.Src - origin - 0.5 / _fps)})*lt(t,{F(sg.Src - origin + sg.Len - 0.5 / _fps)})"));
            chain = $"[{idx}:v]setpts=PTS-STARTPTS,fps={F(_fps)},select={Esc(expr)},setpts=N/({F(_fps)}*TB)";
        }
        g.VideoInput = idx;

        var bw = Even((int)Math.Round(width * clip.Scale));
        var bh = Even((int)Math.Round(height * clip.Scale));
        var sb = new StringBuilder(chain);
        sb.Append($",scale=w={bw}:h={bh}:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuva420p");
        if (clip.Opacity < 1) sb.Append($",colorchannelmixer=aa={F(clip.Opacity)}");
        var dur = g.Duration;
        if (g.First.FadeIn > 0 && g.StartsAtClipStart)
            sb.Append($",fade=t=in:st=0:d={F(Math.Min(g.First.FadeIn, dur))}:alpha=1");
        if (g.Last.FadeOut > 0 && g.EndsAtClipEnd)
        {
            var fo = Math.Min(g.Last.FadeOut, dur);
            sb.Append($",fade=t=out:st={F(dur - fo)}:d={F(fo)}:alpha=1");
        }
        sb.Append($",setpts=PTS+{F(g.TlStart)}/TB");
        var layer = Next("l");
        _statements.Add($"{sb}[{layer}]");
        var output = Next("v");
        _statements.Add($"[{cur}][{layer}]overlay=x={F(clip.X)}*W-w/2:y={F(clip.Y)}*H-h/2:eof_action=pass[{output}]");
        return output;
    }

    private static string[] AlphaDecoder(Asset a) =>
        a.HasAlpha && a.Codec is "vp9" ? ["-c:v", "libvpx-vp9"] :
        a.HasAlpha && a.Codec is "vp8" ? ["-c:v", "libvpx"] : [];

    private string AddText(string cur, Piece p, int width, int height)
    {
        var clip = p.Clip;
        var style = clip.Style ?? new TextStyle();
        var lines = (clip.Text ?? "").Replace("\r", "").Split('\n');
        var fontSize = Math.Max(4, style.FontSize * _scale);
        var lineHeight = fontSize * 1.25;
        var total = lineHeight * lines.Length;
        var a = p.TlStart;
        var b = p.TlStart + p.Dur;
        var alpha = TextAlpha(clip);
        var filters = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            var file = $"text{_graph.TextFiles.Count + 1}.txt";
            _graph.TextFiles[file] = lines[i];
            var y = clip.Y * height - total / 2 + i * lineHeight + (lineHeight - fontSize) / 2;
            var f = new StringBuilder("drawtext=");
            if (!string.IsNullOrEmpty(fontFile)) f.Append($"fontfile={Esc(fontFile.Replace('\\', '/'))}:");
            f.Append($"textfile={Esc(file)}:expansion=none:fontsize={F(fontSize)}:fontcolor={Color(style.Color)}");
            f.Append($":x={F(clip.X * width)}-text_w/2:y={F(y)}");
            if (style.BorderWidth > 0 && !string.IsNullOrEmpty(style.BorderColor))
                f.Append($":borderw={F(style.BorderWidth * _scale)}:bordercolor={Color(style.BorderColor)}");
            if (!string.IsNullOrEmpty(style.BoxColor))
                f.Append($":box=1:boxcolor={Color(style.BoxColor)}:boxborderw={F(style.BoxPadding * _scale)}");
            f.Append($":enable={Esc($"between(t,{F(a)},{F(b)})")}");
            if (alpha is not null) f.Append($":alpha={Esc(alpha)}");
            filters.Add(f.ToString());
        }
        if (filters.Count == 0) return cur;
        var output = Next("t");
        _statements.Add($"[{cur}]{string.Join(",", filters)}[{output}]");
        return output;
    }

    /// <summary>
    /// Maschera di oscuramento: per ogni tratto tra due keyframe si ritaglia la zona (che si sposta
    /// linearmente nel tempo), la si sfoca/pixela/riempie, e la si risovrappone nello stesso punto.
    /// Durante un tratto la dimensione è la massima dei due estremi, così la zona resta sempre coperta.
    /// </summary>
    private string AddMask(string cur, Piece p, int width, int height)
    {
        var m = p.Clip.Mask!;
        var m0 = p.SrcIn;
        var m1 = p.SrcIn + p.Dur;
        var points = new List<double> { m0 };
        points.AddRange(m.Keyframes.Select(k => k.T).Where(t => t > m0 + 1e-6 && t < m1 - 1e-6).Distinct().OrderBy(t => t));
        points.Add(m1);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var ka = m.At(a);
            var kb = m.At(b);
            var ra = p.TlStart + (a - m0);
            var rb = p.TlStart + (b - m0);
            var cw = Math.Clamp(Even((int)Math.Ceiling(Math.Max(ka.W, kb.W) * width)), 2, width - width % 2);
            var ch = Math.Clamp(Even((int)Math.Ceiling(Math.Max(ka.H, kb.H) * height)), 2, height - height % 2);
            var cx = Lerp(ka.X * width, kb.X * width, ra, rb);
            var cy = Lerp(ka.Y * height, kb.Y * height, ra, rb);

            var main = Next("mm");
            var src = Next("ms");
            _statements.Add($"[{cur}]split=2[{main}][{src}]");

            var sb = new StringBuilder($"[{src}]trim=start={F(ra)}:end={F(rb + 0.5 / _fps)}");
            sb.Append($",crop=w={cw}:h={ch}:x={Esc($"clip({cx}-ow/2,0,iw-ow)")}:y={Esc($"clip({cy}-oh/2,0,ih-oh)")}:exact=1");
            switch (m.Effect)
            {
                case MaskEffect.Pixelate:
                    var block = Math.Max(2, m.Strength * _scale);
                    sb.Append($",scale={Math.Max(1, (int)Math.Round(cw / block))}:{Math.Max(1, (int)Math.Round(ch / block))}");
                    sb.Append($",scale={cw}:{ch}:flags=neighbor");
                    break;
                case MaskEffect.Solid:
                    sb.Append($",drawbox=x=0:y=0:w=iw:h=ih:color={Color(m.Color)}:t=fill");
                    break;
                default:
                    sb.Append($",gblur=sigma={F(Math.Max(1, m.Strength * _scale / 2))}:steps=2");
                    break;
            }
            sb.Append(",format=yuva420p");
            var patch = Next("mp");
            _statements.Add($"{sb}[{patch}]");

            if (m.Shape == MaskShape.Ellipse)
            {
                // Canale alfa ellittico calcolato una volta sola e ripetuto per tutta la durata del tratto.
                var alpha = Next("ma");
                var shaped = Next("me");
                var ellipse = Esc("255*lte(pow(X/W*2-1,2)+pow(Y/H*2-1,2),1)");
                _statements.Add($"color=c=black:s={cw}x{ch}:r={F(_fps)}:d={F(1 / _fps)},format=gray,geq=lum={ellipse}," +
                                $"loop=loop=-1:size=1,trim=duration={F(rb - ra + 1 / _fps)},setpts=N/({F(_fps)}*TB)+{F(ra)}/TB[{alpha}]");
                _statements.Add($"[{patch}][{alpha}]alphamerge[{shaped}]");
                patch = shaped;
            }

            var output = Next("mv");
            _statements.Add($"[{main}][{patch}]overlay=x={Esc($"clip({cx}-w/2,0,W-w)")}:y={Esc($"clip({cy}-h/2,0,H-h)")}:eof_action=pass[{output}]");
            cur = output;
        }
        return cur;
    }

    /// <summary>Espressione ffmpeg per un valore che va linearmente da <paramref name="v0"/> a <paramref name="v1"/> tra i tempi indicati.</summary>
    private static string Lerp(double v0, double v1, double t0, double t1) =>
        Math.Abs(v1 - v0) < 1e-6 || t1 - t0 < 1e-6
            ? F(v0)
            : $"({F(v0)}+({F(v1 - v0)})*(t-{F(t0)})/{F(t1 - t0)})";

    private string? TextAlpha(Clip clip)
    {
        // Tempi della clip intera (anche se l'intervallo ne mostra solo una parte).
        var cs = clip.Start - _from;
        var ce = clip.End - _from;
        var fi = Math.Min(clip.FadeIn, clip.Duration);
        var fo = Math.Min(clip.FadeOut, clip.Duration);
        var parts = new List<string>();
        if (fi > 0) parts.Add($"(t-{F(cs)})/{F(fi)}");
        if (fo > 0) parts.Add($"({F(ce)}-t)/{F(fo)}");
        var baseAlpha = clip.Opacity < 1 ? F(clip.Opacity) : null;
        if (parts.Count == 0) return baseAlpha;
        var expr = "1";
        foreach (var part in parts) expr = $"min({expr},{part})";
        expr = $"max(0,{expr})";
        return baseAlpha is null ? expr : $"{baseAlpha}*{expr}";
    }

    // ---------- Audio ----------

    private string? AddAudio(Group g, Track track)
    {
        var asset = g.Pieces[0].Asset!;
        var path = resolvePath(asset.Path);
        var volume = g.First.Volume * track.Volume;
        if (volume <= 0) return null;
        string src;
        if (g.Segments.Count == 1)
        {
            var idx = g.VideoInput ?? AddInput(["-ss", F(g.Segments[0].Src), "-t", F(g.Segments[0].Len), "-i", path]);
            src = $"[{idx}:a]{AudioNormalize()},asetpts=PTS-STARTPTS,atrim=0:{F(g.Segments[0].Len)}";
        }
        else
        {
            var parts = new List<string>();
            foreach (var (s, len) in g.Segments)
            {
                var idx = AddInput(["-ss", F(s), "-t", F(len), "-i", path]);
                var seg = Next("s");
                _statements.Add($"[{idx}:a]{AudioNormalize()},asetpts=PTS-STARTPTS,atrim=0:{F(len)},apad=whole_dur={F(len)}[{seg}]");
                parts.Add($"[{seg}]");
            }
            var joined = Next("j");
            _statements.Add($"{string.Concat(parts)}concat=n={parts.Count}:v=0:a=1[{joined}]");
            src = $"[{joined}]anull";
        }

        var sb = new StringBuilder(src);
        if (Math.Abs(volume - 1) > Eps) sb.Append($",volume={F(volume)}");
        var dur = g.Duration;
        if (g.First.FadeIn > 0 && g.StartsAtClipStart) sb.Append($",afade=t=in:st=0:d={F(Math.Min(g.First.FadeIn, dur))}");
        if (g.Last.FadeOut > 0 && g.EndsAtClipEnd)
        {
            var fo = Math.Min(g.Last.FadeOut, dur);
            sb.Append($",afade=t=out:st={F(dur - fo)}:d={F(fo)}");
        }
        var delayMs = (long)Math.Round(g.TlStart * 1000);
        if (delayMs > 0) sb.Append($",adelay={delayMs}:all=1");
        var output = Next("a");
        _statements.Add($"{sb}[{output}]");
        return output;
    }

    private string AudioNormalize() =>
        $"aresample={project.Settings.SampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo";

    private string MixAudio(List<string> voice, List<string> ducked, double duration, int sampleRate)
    {
        string? Mix(List<string> labels)
        {
            if (labels.Count == 0) return null;
            var output = Next("m");
            var inputs = string.Concat(labels.Select(l => $"[{l}]"));
            _statements.Add(labels.Count == 1
                ? $"{inputs}apad=whole_dur={F(duration)},atrim=0:{F(duration)}[{output}]"
                : $"{inputs}amix=inputs={labels.Count}:duration=longest:normalize=0,apad=whole_dur={F(duration)},atrim=0:{F(duration)}[{output}]");
            return output;
        }

        var v = Mix(voice);
        var m = Mix(ducked);
        string final;
        if (v is null && m is null)
        {
            final = Next("m");
            _statements.Add($"anullsrc=r={sampleRate}:cl=stereo,atrim=0:{F(duration)}[{final}]");
        }
        else if (v is not null && m is not null)
        {
            // Ducking: la musica si abbassa quando c'è parlato sulle altre tracce.
            final = Next("m");
            _statements.Add($"[{v}]asplit=2[{v}a][{v}sc]");
            _statements.Add($"[{m}][{v}sc]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=400[{m}d]");
            _statements.Add($"[{v}a][{m}d]amix=inputs=2:duration=longest:normalize=0[{final}]");
        }
        else
        {
            final = (v ?? m)!;
        }
        _statements.Add($"[{final}]asetpts=PTS-STARTPTS[aout]");
        return "aout";
    }

    // ---------- Supporto ----------

    private int AddInput(IEnumerable<string> args)
    {
        _graph.InputArgs.AddRange(args);
        return _nextInput++;
    }

    private int _nextInput;

    private int CountInputs() => _nextInput;

    private string Next(string prefix) => prefix + (++_label);

    private static int Even(int v) => Math.Max(2, v - v % 2);

    internal static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Converte "#RRGGBB[AA]" nel formato ffmpeg "0xRRGGBB[AA]"; i nomi (es. "white") restano invariati.</summary>
    internal static string Color(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return "0x000000";
        c = c.Trim();
        if (c.StartsWith('#')) c = "0x" + c[1..];
        return Esc(c);
    }

    /// <summary>
    /// Escape a due livelli richiesto da ffmpeg: prima il valore dell'opzione (\ ' :),
    /// poi la descrizione del filtergraph (\ ' [ ] , ;).
    /// </summary>
    internal static string Esc(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (ch is '\\' or '\'' or ':') sb.Append('\\');
            sb.Append(ch);
        }
        var level1 = sb.ToString();
        sb.Clear();
        foreach (var ch in level1)
        {
            if (ch is '\\' or '\'' or '[' or ']' or ',' or ';') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
