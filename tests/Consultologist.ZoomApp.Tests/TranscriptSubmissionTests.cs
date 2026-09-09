using Xunit;

using System.Text;

using Consultologist.ZoomApp.Core.Transcript;

namespace Consultologist.ZoomApp.Tests;

public class TranscriptSubmissionTests
{
    [Fact]
    public void ForTranscript_RidesTheSlotAsAFile_AndMarksItATranscript()
    {
        const string text = "Alice: Good morning.\nBob: How are you?";

        var request = TranscriptSubmission.ForTranscript(text, "consult_draft", "general@v2026.09.1");

        // The transcript is a file on the slot (so the engine extracts it and
        // stamps an origin) — never typed Inputs (which carries no origin).
        Assert.Null(request.Inputs);
        var file = Assert.Single(Assert.Contains("consult_draft", request.InputFiles!));
        Assert.Equal("text/plain", file.ContentType);
        Assert.Equal(text, Encoding.UTF8.GetString(file.Content));

        // The slot is named a transcript, so the engine stamps `transcript`.
        Assert.Equal(new[] { "consult_draft" }, request.TranscriptInputs);

        Assert.Equal("general@v2026.09.1", request.WorkflowPackage);
    }

    [Fact]
    public void ForTranscript_WithoutAPackageRef_LetsTheEngineUseThePin()
    {
        var request = TranscriptSubmission.ForTranscript("x", "slot");

        Assert.Null(request.WorkflowPackage);
        Assert.Equal(new[] { "slot" }, request.TranscriptInputs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ForTranscript_RefusesABlankSlot(string slot)
    {
        Assert.Throws<ArgumentException>(() => TranscriptSubmission.ForTranscript("text", slot));
    }
}
