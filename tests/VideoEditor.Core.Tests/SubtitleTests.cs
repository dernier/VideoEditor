using System;
using System.Collections.Generic;
using System.Linq;
using VideoEditor.Core.Editing;
using VideoEditor.Core.Model;
using VideoEditor.Core.Transcription;

namespace VideoEditor.Core.Tests;

public class SubtitleTests
{
    private static List<Word> Words(params (string Text, double Start, double End)[] w) =>
        w.Select(x => new Word { Text = x.Text, Start = x.Start, End = x.End }).ToList();

    [Fact]
    public void Build_BreaksOnSentencesAndPauses()
    {
        var cues = SubtitleBuilder.Build(Words(
            ("Ciao", 0.0, 0.3), ("a", 0.35, 0.4), ("tutti.", 0.45, 0.8),
            ("Oggi", 1.0, 1.3), ("parliamo", 1.35, 1.8),
            ("di", 3.5, 3.6), ("video", 3.65, 4.0)));
        Assert.Equal(["Ciao a tutti.", "Oggi parliamo", "di video"], cues.Select(c => c.Text).ToArray());
        Assert.Equal(0.0, cues[0].Start);
        Assert.True(cues[0].End <= cues[1].Start - 0.03, "i sottotitoli non si sovrappongono");
        Assert.True(cues[2].End - cues[2].Start >= 0.9, "durata minima");
    }

    [Fact]
    public void Build_WrapsAndBalancesLines()
    {
        var words = new List<Word>();
        var t = 0.0;
        foreach (var w in "questa è una frase abbastanza lunga da andare su due righe bilanciate".Split(' '))
        {
            words.Add(new Word { Text = w, Start = t, End = t + 0.2 });
            t += 0.25;
        }
        var cue = Assert.Single(SubtitleBuilder.Build(words, new SubtitleOptions { MaxChars = 42 }));
        var lines = cue.Text.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= 42));
        Assert.True(Math.Abs(lines[0].Length - lines[1].Length) <= 10, cue.Text);
    }

    [Fact]
    public void Build_SplitsWhenTooLongAndSkipsFillers()
    {
        var words = new List<Word>();
        for (var i = 0; i < 40; i++) words.Add(new Word { Text = i == 3 ? "ehm" : "parola", Start = i * 0.3, End = i * 0.3 + 0.25 });
        var cues = SubtitleBuilder.Build(words, new SubtitleOptions { MaxChars = 20, MaxDuration = 6 });
        Assert.True(cues.Count > 1);
        Assert.All(cues, c => Assert.All(c.Text.Split('\n'), l => Assert.True(l.Length <= 20, l)));
        Assert.All(cues, c => Assert.True(c.Text.Split('\n').Length <= 2));
        Assert.DoesNotContain(cues, c => c.Text.Contains("ehm"));
    }

    [Fact]
    public void SetSubtitles_CreatesTrackAndKeepsStyleOnRebuild()
    {
        var p = TimelineEditorTests.Sample();
        var e = new TimelineEditor(p);
        var cues = new List<SubtitleCue> { new(0, 2, "uno"), new(2.5, 4, "due") };
        var track = e.SetSubtitles(cues);
        Assert.True(track.Subtitles);
        Assert.Equal(0, p.Tracks.IndexOf(track));
        Assert.Equal(2, track.Clips.Count);

        // L'utente cambia lo stile di un sottotitolo e lo applica a tutti...
        var first = track.Clips[0];
        first.Style!.FontSize = 70;
        first.Y = 0.1;
        Assert.Equal(2, e.ApplyStyleToTrack(first.Id));
        Assert.Equal(70, track.Clips[1].Style!.FontSize);
        Assert.Equal(0.1, track.Clips[1].Y);

        // ...e rigenerando i sottotitoli lo stile resta, sulla stessa traccia.
        var again = e.SetSubtitles([new SubtitleCue(0, 1, "tre")]);
        Assert.Same(track, again);
        Assert.Equal(70, again.Clips.Single().Style!.FontSize);
        Assert.Equal(0.1, again.Clips.Single().Y);
        Assert.Equal("tre", e.CurrentSubtitles().Single().Text);
    }

    [Fact]
    public void Srt_UsesCrLfAndCommaMilliseconds()
    {
        var srt = SubtitleBuilder.ToSrt([new SubtitleCue(61.5, 63.25, "riga uno\nriga due")]);
        Assert.Equal("1\r\n00:01:01,500 --> 00:01:03,250\r\nriga uno\r\nriga due\r\n\r\n", srt);
    }
}
