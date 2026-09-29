using System;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Model;
using VideoEditor.Core.Render;

namespace VideoEditor.Core.Tests;

public class RenderGraphTests
{
    private static RenderGraph Build(Project p, RenderRequest? r = null) =>
        new RenderGraphBuilder(p, x => "/media/" + x, "C:\\Windows\\Fonts\\arial.ttf").Build(r ?? new RenderRequest());

    [Fact]
    public void Escape_HandlesBothLevels()
    {
        Assert.Equal(@"C\\:/Windows/Fonts/arial.ttf", RenderGraphBuilder.Esc("C:/Windows/Fonts/arial.ttf"));
        Assert.Equal(@"between(t\,1\,2)", RenderGraphBuilder.Esc("between(t,1,2)"));
        Assert.Equal(@"it\\\'s", RenderGraphBuilder.Esc("it's"));
    }

    [Fact]
    public void Color_ConvertsHash()
    {
        Assert.Equal("0x000000AA", RenderGraphBuilder.Color("#000000AA"));
        Assert.Equal("white", RenderGraphBuilder.Color("white"));
    }

    [Fact]
    public void ContiguousCuts_ShareOneVideoInput()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 30);
        e.RippleDeleteRanges([(2, 3), (10, 12), (20, 21)]);
        Assert.Equal(4, p.FindTrack("V1")!.Clips.Count);

        var g = Build(p);
        // 1 input video per il gruppo + 1 input audio per ciascun segmento.
        Assert.Equal(5, g.InputCount);
        Assert.Contains("select=", g.Filter);
        Assert.Contains("concat=n=4:v=0:a=1", g.Filter);
        Assert.Equal(26, g.Duration, 6);
    }

    [Fact]
    public void NonContiguousClips_UseSeparateInputs()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 5);
        e.AddClip("V1", "m1", 6, 10, 5); // spazio vuoto tra le due
        var g = Build(p);
        Assert.Equal(2, g.InputCount);
        Assert.DoesNotContain("select=", g.Filter);
        Assert.Contains("adelay=6000:all=1", g.Filter);
    }

    [Fact]
    public void TopTrackIsOverlaidLast()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 5);
        var logo = e.AddClip("V2", "m3", 1, 0, 2);
        logo.Scale = 0.25;
        var g = Build(p);
        var videoInput = g.Filter.IndexOf("[0:v]", StringComparison.Ordinal);
        var logoInput = g.Filter.IndexOf("[1:v]", StringComparison.Ordinal);
        Assert.True(videoInput >= 0 && logoInput > videoInput);
        Assert.Contains("-loop", g.InputArgs);
    }

    [Fact]
    public void Text_UsesTextFileAndFades()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 5);
        var t = e.AddText(null, "Riga uno\nRiga: due", 1, 3);
        t.FadeIn = 0.5;
        var g = Build(p);
        Assert.Equal(2, g.TextFiles.Count);
        Assert.Equal("Riga: due", g.TextFiles["text2.txt"]);
        Assert.Contains("expansion=none", g.Filter);
        Assert.Contains(@"between(t\,1\,4)", g.Filter);
        Assert.Contains("alpha=", g.Filter);
    }

    [Fact]
    public void DuckedTrack_UsesSidechain()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 5);
        e.AddClip("A1", "m2", 0, 0, 5);
        var g = Build(p);
        Assert.Contains("sidechaincompress", g.Filter);
        Assert.Equal("aout", g.AudioLabel);
    }

    [Fact]
    public void RangeRender_ClipsToInterval()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 10, 20);
        var g = Build(p, new RenderRequest { From = 5, To = 5 + 1 / 30.0, Audio = false, Height = 540 });
        Assert.Equal(960, g.Width);
        Assert.Contains("15", g.InputArgs[g.InputArgs.IndexOf("-ss") + 1]);
        Assert.Null(g.AudioLabel);
    }

    [Fact]
    public void MutedAndHiddenTracks_AreSkipped()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 5);
        p.FindTrack("V1")!.Hidden = true;
        p.FindTrack("V1")!.Muted = true;
        var g = Build(p);
        Assert.Equal(0, g.InputCount);
        Assert.Contains("anullsrc", g.Filter);
    }
}
