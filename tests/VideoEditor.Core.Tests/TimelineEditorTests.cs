using System;
using System.Linq;
using System.Text.Json.Nodes;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Model;

namespace VideoEditor.Core.Tests;

public class TimelineEditorTests
{
    internal static Project Sample()
    {
        var p = Project.CreateDefault("test");
        p.Assets.Add(new Asset { Id = "m1", Kind = AssetKind.Video, Duration = 60, HasAudio = true, HasVideo = true, Path = "a.mp4" });
        p.Assets.Add(new Asset { Id = "m2", Kind = AssetKind.Audio, Duration = 100, HasAudio = true, Path = "m.mp3" });
        p.Assets.Add(new Asset { Id = "m3", Kind = AssetKind.Image, HasVideo = true, Path = "l.png" });
        return p;
    }

    [Fact]
    public void AddClip_AppendsAtTrackEnd()
    {
        var e = new TimelineEditor(Sample());
        var c1 = e.AddClip("V1", "m1", inPoint: 10, duration: 5);
        var c2 = e.AddClip("V1", "m1");
        Assert.Equal(0, c1.Start);
        Assert.Equal(5, c2.Start);
        Assert.Equal(60, c2.Duration);
        Assert.Equal("c2", c2.Id);
    }

    [Fact]
    public void AddClip_RejectsAudioOnVideoTrack()
    {
        var e = new TimelineEditor(Sample());
        Assert.Throws<EditException>(() => e.AddClip("V1", "m2"));
    }

    [Fact]
    public void Split_KeepsSourceContinuity()
    {
        var e = new TimelineEditor(Sample());
        var c = e.AddClip("V1", "m1", 2, 10, 20);
        var right = e.SplitClip(c.Id, 7);
        Assert.Equal(5, c.Duration, 6);
        Assert.Equal(7, right.Start, 6);
        Assert.Equal(15, right.In, 6);
        Assert.Equal(15, right.Duration, 6);
    }

    [Fact]
    public void RippleDelete_TrimsSplitsAndShifts()
    {
        var p = Sample();
        var e = new TimelineEditor(p);
        var a = e.AddClip("V1", "m1", 0, 0, 10);   // 0-10
        var b = e.AddClip("V1", "m1", 10, 20, 10); // 10-20
        e.AddMarker(15, "m");
        e.RippleDelete(4, 12);

        var clips = p.FindTrack("V1")!.Clips;
        Assert.Equal(2, clips.Count);
        Assert.Equal(4, a.Duration, 6);
        Assert.Equal(4, b.Start, 6);
        Assert.Equal(22, b.In, 6);
        Assert.Equal(8, b.Duration, 6);
        Assert.Equal(7, p.Markers[0].Time, 6);
        Assert.Equal(12, p.GetDuration(), 6);
    }

    [Fact]
    public void RippleDelete_InsideClipCreatesTwoPieces()
    {
        var p = Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 30);
        e.RippleDelete(10, 12);
        var clips = p.FindTrack("V1")!.Clips;
        Assert.Equal(2, clips.Count);
        Assert.Equal(10, clips[0].Duration, 6);
        Assert.Equal(10, clips[1].Start, 6);
        Assert.Equal(12, clips[1].In, 6);
        Assert.Equal(18, clips[1].Duration, 6);
        Assert.Empty(e.Validate());
    }

    [Fact]
    public void RippleDeleteRanges_OrderIndependentAndMerged()
    {
        var p = Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 30);
        e.RippleDeleteRanges([(2, 3), (20, 25), (2.5, 4)]);
        Assert.Equal(30 - 2 - 5, p.GetDuration(), 6);
        var clips = p.FindTrack("V1")!.Clips;
        Assert.Equal([0.0, 4.0, 25.0], clips.Select(c => c.In).ToArray());
    }

    [Fact]
    public void RippleDelete_SkipsLockedTracks()
    {
        var p = Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 30);
        var music = e.AddClip("A1", "m2", 0, 0, 30);
        p.FindTrack("A1")!.Locked = true;
        e.RippleDelete(5, 10);
        Assert.Equal(30, music.Duration);
        Assert.Equal(25, p.FindTrack("V1")!.Clips.Sum(c => c.Duration), 6);
    }

    [Fact]
    public void Trim_StartMovesInPoint()
    {
        var e = new TimelineEditor(Sample());
        var c = e.AddClip("V1", "m1", 5, 10, 10);
        e.TrimClip(c.Id, 7, 12);
        Assert.Equal(7, c.Start, 6);
        Assert.Equal(12, c.In, 6);
        Assert.Equal(5, c.Duration, 6);
    }

    [Fact]
    public void Trim_CannotExtendBeforeSourceStart()
    {
        var e = new TimelineEditor(Sample());
        var c = e.AddClip("V1", "m1", 5, 1, 10);
        e.TrimClip(c.Id, 0, null);
        Assert.Equal(0, c.In, 6);
        Assert.Equal(4, c.Start, 6);
        Assert.Equal(11, c.Duration, 6);
    }

    [Fact]
    public void SetClip_AppliesPatchAndRejectsUnknownKeys()
    {
        var e = new TimelineEditor(Sample());
        var t = e.AddText(null, "ciao", 1, 2);
        e.SetClip(t.Id, JsonNode.Parse("""{"volume":0.5,"style":{"fontSize":90},"x":0.2}""")!.AsObject());
        Assert.Equal(0.5, t.Volume);
        Assert.Equal(90, t.Style!.FontSize);
        Assert.Equal("#FFFFFF", t.Style.Color);
        Assert.Equal(0.2, t.X);
        Assert.Throws<ArgumentException>(() => e.SetClip(t.Id, JsonNode.Parse("""{"dur":3}""")!.AsObject()));
    }

    [Fact]
    public void MoveClip_BetweenCompatibleTracksOnly()
    {
        var e = new TimelineEditor(Sample());
        var c = e.AddClip("V1", "m3", 0);
        e.MoveClip(c.Id, 3, "V2");
        Assert.Equal("V2", e.Project.FindClip(c.Id)!.Value.Track.Id);
        Assert.Throws<EditException>(() => e.MoveClip(c.Id, 3, "A1"));
    }

    [Fact]
    public void Validate_ReportsOverlaps()
    {
        var e = new TimelineEditor(Sample());
        e.AddClip("V1", "m1", 0, 0, 10);
        e.AddClip("V1", "m1", 5, 0, 10);
        Assert.Contains(e.Validate(), i => i.Contains("sovrappongono"));
    }

    [Fact]
    public void InsertGap_ShiftsLaterClips()
    {
        var p = Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 10);
        e.InsertGap(4, 2);
        var clips = p.FindTrack("V1")!.Clips;
        Assert.Equal(6, clips[1].Start, 6);
        Assert.Equal(4, clips[1].In, 6);
        Assert.Equal(12, p.GetDuration(), 6);
    }
}
