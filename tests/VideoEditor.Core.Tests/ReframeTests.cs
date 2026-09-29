using System;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Tests;

public class ReframeTests
{
    [Theory]
    [InlineData("16:9", 1920, 1080)]
    [InlineData("9:16", 1080, 1920)]
    [InlineData("2.35:1", 2538, 1080)]
    [InlineData("1:1", 1080, 1080)]
    [InlineData("4:3", 1440, 1080)]
    [InlineData("5.8in", 1080, 2338)]
    public void SizeFor_KeepsShortSide(string ratio, int w, int h)
    {
        Assert.Equal((w, h), Reframe.SizeFor(Reframe.ParseRatio(ratio), 1080));
    }

    [Theory]
    [InlineData(0.5, 0.5, 1.0)]
    [InlineData(0.8, 0.3, 0.3)]
    [InlineData(0.2, 0.7, 2.5)]
    public void Transform_RoundTrip(double fx, double fy, double fw)
    {
        var (s, x, y) = Reframe.ToTransform(fx, fy, fw, 1080, 1920, 1920, 1080);
        var (rx, ry, rw) = Reframe.FromTransform(s, x, y, 1080, 1920, 1920, 1080);
        Assert.Equal(fx, rx, 6);
        Assert.Equal(fy, ry, 6);
        Assert.Equal(fw, rw, 6);
    }

    [Fact]
    public void FitAndFill_ForVerticalOutputOfHorizontalVideo()
    {
        // 16:9 in 9:16: "adatta" = scala 1 (tutto il video, bande), "riempi" = ritaglio centrale alto quanto il video.
        var fit = Reframe.FitWidth(1080, 1920, 1920, 1080);
        Assert.Equal(1, fit, 6);
        Assert.Equal(1, Reframe.ToTransform(0.5, 0.5, fit, 1080, 1920, 1920, 1080).Scale, 6);

        var fill = Reframe.FillWidth(1080, 1920, 1920, 1080);
        Assert.Equal(1080 * (1080.0 / 1920) / 1920, fill, 6); // cornice larga 607,5 px su 1920
        var (s, x, y) = Reframe.ToTransform(0.5, 0.5, fill, 1080, 1920, 1920, 1080);
        // Adattato il video è 1080×607,5; per riempire deve diventare alto 1920: scala 1920/607,5 = 3,16.
        Assert.Equal(1920 / (1080 * 1080.0 / 1920), s, 4);
        Assert.Equal((0.5, 0.5), (x, y));
    }

    [Fact]
    public void ApplyAspect_ResetsMainClipsAndKeepsPictureInPicture()
    {
        var p = TimelineEditorTests.Sample();
        p.Assets[0].Width = 1920;
        p.Assets[0].Height = 1080;
        var e = new TimelineEditor(p);
        var main = e.AddClip("V1", "m1", 0, 0, 10);
        var pip = e.AddClip("V2", "m1", 0, 0, 10);
        pip.Scale = 0.3;
        pip.X = 0.8;

        var (w, h, n) = e.ApplyAspect(9.0 / 16, fill: true);
        Assert.Equal((1080, 1920, 1), (w, h, n));
        Assert.Equal(1920 / (1080 * 1080.0 / 1920), main.Scale, 3);
        Assert.Equal(0.3, pip.Scale);

        // Reinquadra solo a destra: la cornice larga 0,3 centrata a x=0,8.
        var changed = e.ReframeClip(main.Id, 0.8, 0.5, 0.3);
        Assert.Single(changed);
        Assert.True(main.X < 0.5, "per mostrare la parte destra il video si sposta a sinistra");
    }

    [Fact]
    public void Reframe_AllPiecesOfTheSameVideo()
    {
        var p = TimelineEditorTests.Sample();
        p.Assets[0].Width = 1920;
        p.Assets[0].Height = 1080;
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 30);
        e.RippleDeleteRanges([(5, 6), (12, 13)]);
        var pieces = p.FindTrack("V1")!.Clips;
        Assert.Equal(3, pieces.Count);
        e.ReframeFill(pieces[1].Id, fill: true, allPieces: true);
        Assert.All(pieces, c => Assert.Equal(pieces[1].Scale, c.Scale));
    }

    [Fact]
    public void Probe_SwapsDimensionsOfRotatedPhoneVideo()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"hevc","width":1920,"height":1080,"avg_frame_rate":"30/1",
                         "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]}],
             "format":{"duration":"10"}}
            """;
        var a = Media.MediaProbe.Parse(json, "/x/IMG_0001.MOV");
        Assert.Equal((1080, 1920), (a.Width, a.Height));
    }
}
