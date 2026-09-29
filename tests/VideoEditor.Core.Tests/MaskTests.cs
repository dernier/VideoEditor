using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Media;
using VideoEditor.Core.Model;
using VideoEditor.Core.Render;

namespace VideoEditor.Core.Tests;

public class MaskTests
{
    [Fact]
    public void At_InterpolatesAndHoldsEnds()
    {
        var m = new MaskSpec
        {
            Keyframes =
            [
                new MaskKey { T = 1, X = 0.2, Y = 0.5, W = 0.1, H = 0.1 },
                new MaskKey { T = 3, X = 0.6, Y = 0.5, W = 0.3, H = 0.1 },
            ],
        };
        Assert.Equal(0.2, m.At(0).X, 6);
        Assert.Equal(0.4, m.At(2).X, 6);
        Assert.Equal(0.2, m.At(2).W, 6);
        Assert.Equal(0.6, m.At(10).X, 6);
    }

    [Fact]
    public void AddMask_CreatesTrackAboveVideoWhenMissing()
    {
        var p = TimelineEditorTests.Sample();
        p.Tracks.RemoveAll(t => t.Kind == TrackKind.Mask);
        var e = new TimelineEditor(p);
        var c = e.AddMask(null, 2, 3);
        var track = p.FindClip(c.Id)!.Value.Track;
        Assert.Equal(TrackKind.Mask, track.Kind);
        Assert.True(p.Tracks.IndexOf(track) < p.Tracks.FindIndex(t => t.Kind == TrackKind.Video));
        Assert.True(p.Tracks.IndexOf(track) > p.Tracks.FindIndex(t => t.Kind == TrackKind.Text));
    }

    [Fact]
    public void SetMaskRect_StaticThenKeyframes()
    {
        var e = new TimelineEditor(TimelineEditorTests.Sample());
        var c = e.AddMask(null, 10, 4);
        e.SetMaskRect(c.Id, 11, 0.3, 0.4, 0.2, 0.2);
        Assert.Empty(c.Mask!.Keyframes);
        Assert.Equal(0.3, c.Mask.X);

        // Primo keyframe a metà: viene fissata anche la posizione iniziale.
        e.SetMaskRect(c.Id, 12, 0.7, 0.4, 0.2, 0.2, asKeyframe: true);
        Assert.Equal(2, c.Mask.Keyframes.Count);
        Assert.Equal(0.3, c.Mask.At(c.In).X, 6);
        Assert.Equal(0.7, c.Mask.At(c.In + 2).X, 6);

        // Con keyframe presenti, spostare la zona crea/aggiorna un keyframe.
        e.SetMaskRect(c.Id, 12, 0.8, 0.4, 0.2, 0.2);
        Assert.Equal(2, c.Mask.Keyframes.Count);
        Assert.Equal(0.8, c.Mask.At(c.In + 2).X, 6);

        e.RemoveMaskKey(c.Id, 12);
        Assert.Empty(c.Mask.Keyframes);
        Assert.Equal(0.3, c.Mask.X, 6);
    }

    [Fact]
    public void SplitAndRipple_PreserveMotion()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        var c = e.AddMask(null, 0, 10);
        e.SetMaskRect(c.Id, 0, 0.1, 0.5, 0.2, 0.2, asKeyframe: true);
        e.SetMaskRect(c.Id, 10, 0.9, 0.5, 0.2, 0.2, asKeyframe: true);
        double XAt(Clip clip, double t) => clip.Mask!.At(clip.In + (t - clip.Start)).X;

        var right = e.SplitClip(c.Id, 5);
        Assert.Equal(0.5, XAt(right, 5), 6);
        Assert.Equal(0.1 + 0.8 * 0.7, XAt(right, 7), 6);

        // Tagliando 2-3 s, ciò che era a 4 s si trova a 3 s con la stessa posizione.
        e.RippleDelete(2, 3);
        var at3 = TimelineEditor.ClipAt(p.FindTrack("M1")!, 3)!;
        Assert.NotEqual(c.Id, at3.Id);
        Assert.Equal(0.1 + 0.8 * 0.4, XAt(at3, 3), 6);
        Assert.Empty(e.Validate());
    }

    [Fact]
    public void Mask_OnlyOnMaskTracks()
    {
        var e = new TimelineEditor(TimelineEditorTests.Sample());
        var c = e.AddMask(null, 0, 2);
        Assert.Throws<EditException>(() => e.MoveClip(c.Id, 0, "V1"));
        Assert.Throws<EditException>(() => e.AddClip("M1", "m1"));
    }

    [Fact]
    public void RenderGraph_MaskFilters()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 10);
        var blur = e.AddMask(null, 1, 4, new MaskSpec { X = 0.5, Y = 0.5, W = 0.2, H = 0.2 });
        e.SetMaskRect(blur.Id, 3, 0.7, 0.5, 0.2, 0.2, asKeyframe: true);
        e.AddMask(null, 2, 2, new MaskSpec { Effect = MaskEffect.Pixelate, Shape = MaskShape.Rect });
        e.AddMask(null, 2, 2, new MaskSpec { Effect = MaskEffect.Solid, Shape = MaskShape.Rect, Color = "#112233" });

        var g = new RenderGraphBuilder(p, x => x, null).Build(new RenderRequest());
        Assert.Contains("gblur=sigma=", g.Filter);
        Assert.Contains("alphamerge", g.Filter);
        Assert.Contains("flags=neighbor", g.Filter);
        Assert.Contains("color=0x112233:t=fill", g.Filter);
        // Due tratti per la maschera con keyframe: il primo si muove (espressione con t), il secondo è fermo.
        Assert.Contains(@"(t-1)", g.Filter);
        Assert.Equal(4, g.Filter.Split('\n').Count(l => l.Contains("overlay=x=clip")));
        // Le maschere stanno sopra il video e sotto i testi.
        Assert.True(g.Filter.IndexOf("gblur", StringComparison.Ordinal) > g.Filter.IndexOf("[0:v]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Render_BlursOnlyInsideMask()
    {
        string ffmpeg;
        try { ffmpeg = Ffmpeg.FfmpegPath; }
        catch (FileNotFoundException) { return; } // ffmpeg non installato: test saltato

        var dir = Path.Combine(Path.GetTempPath(), "vedit-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Sorgente con dettagli fini ovunque (rumore): la sfocatura li cancella.
            var src = Path.Combine(dir, "noise.mp4");
            var gen = Process.Start(new ProcessStartInfo(ffmpeg,
                ["-v", "error", "-y", "-f", "lavfi", "-i", "nullsrc=s=320x240:r=10:d=2,geq=random(1)*255:128:128", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-qp", "0", src]))!;
            await gen.WaitForExitAsync();
            Assert.Equal(0, gen.ExitCode);

            var store = ProjectStore.Create(dir, Project.CreateDefault("t"));
            var p = store.Load();
            var asset = AssetImporter.Import(store, p, src);
            AssetImporter.AdoptFormatIfEmpty(p, asset);
            var e = new TimelineEditor(p);
            e.AddClip("V1", asset.Id, 0);
            e.AddMask(null, 0, 2, new MaskSpec { Shape = MaskShape.Rect, X = 0.25, Y = 0.5, W = 0.5, H = 1, Strength = 20 });

            var png = Path.Combine(dir, "f.png");
            await Renderer.RenderFrameAsync(store, p, 1, png);
            var raw = Path.Combine(dir, "f.gray");
            var conv = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-y", "-i", png, "-pix_fmt", "gray", "-f", "rawvideo", raw]))!;
            await conv.WaitForExitAsync();
            var px = File.ReadAllBytes(raw);
            Assert.Equal(320 * 240, px.Length);

            // Variazione media tra pixel vicini: bassa a sinistra (sfocata), alta a destra (rumore).
            double Detail(int x0, int x1)
            {
                double sum = 0; var n = 0;
                for (var y = 20; y < 220; y++)
                    for (var x = x0; x < x1 - 1; x++) { sum += Math.Abs(px[y * 320 + x] - px[y * 320 + x + 1]); n++; }
                return sum / n;
            }
            var left = Detail(20, 140);
            var right = Detail(180, 300);
            Assert.True(left * 4 < right, $"sinistra {left:0.0}, destra {right:0.0}");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClaudeMd_UpdateKeepsUserNotes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vedit-md-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "CLAUDE.md");
            File.WriteAllText(path, "# Progetto video — istruzioni per Claude\nvecchio testo\n## Note del progetto\nIl cliente si chiama Rossi.\n");
            Templates.UpdateProjectClaudeMd(dir);
            var text = File.ReadAllText(path);
            Assert.Contains("vedit mask add", text);
            Assert.EndsWith("Il cliente si chiama Rossi.\n", text);
            Assert.DoesNotContain("vecchio testo", text);

            File.WriteAllText(path, "# Il mio CLAUDE.md\n");
            Templates.UpdateProjectClaudeMd(dir);
            Assert.Equal("# Il mio CLAUDE.md\n", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
