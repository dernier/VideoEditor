using VideoEditor.Core.Editing;
using VideoEditor.Core.Model;
using VideoEditor.Core.Transcription;

namespace VideoEditor.Core.Tests;

public class TranscriptAndStoreTests
{
    private static Transcript SampleTranscript() => new()
    {
        AssetId = "m1",
        Words =
        [
            new Word { Text = "Ciao", Start = 0.5, End = 0.9 },
            new Word { Text = "ehm,", Start = 1.0, End = 1.4 },
            new Word { Text = "oggi", Start = 1.5, End = 1.8 },
            new Word { Text = "parliamo", Start = 3.0, End = 3.5 },
            new Word { Text = "di", Start = 3.6, End = 3.7 },
            new Word { Text = "video.", Start = 3.8, End = 4.2 },
        ],
    };

    [Fact]
    public void ToTimeline_FollowsCuts()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        e.AddClip("V1", "m1", 0, 0, 10);
        e.RippleDelete(1.9, 2.9); // toglie la pausa tra "oggi" e "parliamo"
        var words = TranscriptTools.ToTimeline(p, id => id == "m1" ? SampleTranscript() : null);
        Assert.Equal(6, words.Count);
        var parliamo = words.Single(w => w.Text == "parliamo");
        Assert.Equal(2.0, parliamo.Start, 3);
    }

    [Fact]
    public void Readable_MarksPausesAndFillers()
    {
        var text = TranscriptTools.FormatReadable(SampleTranscript().Words);
        Assert.Contains("«ehm,@1.00–1.40»", text);
        Assert.Contains("⟨pausa 1.20s 1.80–3.00⟩", text);
    }

    [Fact]
    public void Find_IgnoresCaseAndPunctuation()
    {
        var hits = TranscriptTools.Find(SampleTranscript().Words, "di Video");
        Assert.Single(hits);
        Assert.Equal(3.6, hits[0].Start);
        Assert.Equal(4.2, hits[0].End);
    }

    [Fact]
    public void Fillers_ExtendToNextWord()
    {
        var hits = TranscriptTools.FindFillers(SampleTranscript().Words);
        Assert.Single(hits);
        Assert.Equal((1.0, 1.5), (hits[0].Start, hits[0].End));
    }

    [Fact]
    public void Srt_HasTimecodes()
    {
        var srt = TranscriptTools.ToSrt(SampleTranscript().Words);
        Assert.StartsWith("1\n00:00:00,500 --> ", srt.Replace("\r", ""));
    }

    [Fact]
    public void MergeFragments_JoinsTokensWithoutLeadingSpace()
    {
        var merged = WhisperTranscriber.MergeFragments(
        [
            new Word { Text = " perch", Start = 0, End = 0.2 },
            new Word { Text = "é", Start = 0.2, End = 0.3 },
            new Word { Text = " no", Start = 0.4, End = 0.5 },
            new Word { Text = "?", Start = 0.5, End = 0.5 },
        ]);
        Assert.Equal(["perché", "no?"], merged.Select(w => w.Text).ToArray());
        Assert.Equal(0.3, merged[0].End);
    }

    [Fact]
    public void Store_UndoRedo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vedit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = ProjectStore.Create(dir, Project.CreateDefault("x"));
            Assert.True(File.Exists(Path.Combine(dir, "CLAUDE.md")));
            var p = store.Load();
            p.Name = "uno";
            store.Save(p, "rinomina 1");
            p.Name = "due";
            store.Save(p, "rinomina 2");

            Assert.Equal("uno", store.Undo()!.Name);
            Assert.Equal("x", store.Undo()!.Name);
            Assert.Null(store.Undo());
            Assert.Equal("uno", store.Redo()!.Name);
            Assert.Equal("uno", store.Load().Name);

            p.Name = "tre";
            store.Save(p, "rinomina 3"); // elimina il "ripeti"
            Assert.Null(store.Redo());
            Assert.Equal(dir, ProjectStore.Find(Path.Combine(dir, "media"))!.Directory);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Probe_ParsesFfprobeJson()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"hevc","width":3840,"height":2160,"avg_frame_rate":"60000/1001","pix_fmt":"yuv420p10le"},
                        {"codec_type":"audio","codec_name":"aac"}],
             "format":{"duration":"125.5"}}
            """;
        var a = Media.MediaProbe.Parse(json, "/x/clip.MP4");
        Assert.Equal(AssetKind.Video, a.Kind);
        Assert.Equal(59.94, a.Fps, 2);
        Assert.True(a.HasAudio);
        Assert.False(a.HasAlpha);
        Assert.Equal(125.5, a.Duration);
    }
}
