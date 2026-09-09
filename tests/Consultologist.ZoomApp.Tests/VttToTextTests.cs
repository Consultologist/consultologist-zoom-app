using Xunit;

using Consultologist.ZoomApp.Core.Transcript;

namespace Consultologist.ZoomApp.Tests;

public class VttToTextTests
{
    [Fact]
    public void ZoomStyleVtt_KeepsSpeakerLabels_DropsTimingsAndIds()
    {
        const string vtt =
            "WEBVTT\n\n" +
            "1\n00:00:01.000 --> 00:00:04.000\nAlice Smith: Good morning.\n\n" +
            "2\n00:00:04.500 --> 00:00:07.000\nBob Jones: How are you feeling?\n";

        var text = VttToText.ToText(vtt);

        Assert.Equal("Alice Smith: Good morning.\nBob Jones: How are you feeling?", text);
    }

    [Fact]
    public void VoiceTags_BecomeSpeakerColonText()
    {
        const string vtt = "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n<v Dr Lee>The referral is for chest pain.</v>\n";

        Assert.Equal("Dr Lee: The referral is for chest pain.", VttToText.ToText(vtt));
    }

    [Fact]
    public void NoteAndStyleBlocks_AreSkipped()
    {
        const string vtt =
            "WEBVTT\n\n" +
            "NOTE this recording was transcribed by Zoom\n\n" +
            "1\n00:00:01.000 --> 00:00:02.000\nOnly this line.\n";

        Assert.Equal("Only this line.", VttToText.ToText(vtt));
    }

    [Fact]
    public void MultiLineCue_JoinsWithSpaces()
    {
        const string vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:05.000\nFirst part\nsecond part\n";

        Assert.Equal("First part second part", VttToText.ToText(vtt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WEBVTT\n")]
    public void EmptyOrHeaderOnly_YieldsNothing(string? vtt)
    {
        Assert.Equal(string.Empty, VttToText.ToText(vtt));
    }

    [Fact]
    public void CrlfLineEndings_AreHandled()
    {
        const string vtt = "WEBVTT\r\n\r\n1\r\n00:00:01.000 --> 00:00:02.000\r\nAlice: Hi.\r\n";

        Assert.Equal("Alice: Hi.", VttToText.ToText(vtt));
    }
}
