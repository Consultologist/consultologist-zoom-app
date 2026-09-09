using System.Text;

using Consultologist.ZoomApp.Core.Engine;

namespace Consultologist.ZoomApp.Core.Transcript;

/// <summary>
/// Builds the <see cref="ConsultGenerationRequest"/> that submits a meeting
/// transcript as the signed-in clinician. The transcript rides an existing
/// declared file slot as a <c>text/plain</c> document, and its slot id is named
/// in <see cref="ConsultGenerationRequest.TranscriptInputs"/> so the engine
/// stamps the <c>transcript</c> origin (provenance@v2026.09.6) rather than
/// <c>document</c>. Nothing else about the request is special — the engine
/// extracts, hashes and records provenance itself (the verifiability boundary).
/// </summary>
public static class TranscriptSubmission
{
    /// <summary>
    /// A request that puts <paramref name="transcriptText"/> into
    /// <paramref name="slotId"/> as a transcript. <paramref name="workflowPackageRef"/>
    /// is echoed when known (null lets the engine use the account's pin).
    /// </summary>
    /// <exception cref="ArgumentException">The slot id is blank.</exception>
    public static ConsultGenerationRequest ForTranscript(
        string transcriptText, string slotId, string? workflowPackageRef = null)
    {
        if (string.IsNullOrWhiteSpace(slotId))
        {
            throw new ArgumentException("A transcript needs a declared slot id.", nameof(slotId));
        }

        var bytes = Encoding.UTF8.GetBytes(transcriptText ?? string.Empty);

        return new ConsultGenerationRequest
        {
            InputFiles = new Dictionary<string, List<InputFilePayload>>(StringComparer.Ordinal)
            {
                [slotId] = new() { new InputFilePayload("text/plain", bytes) },
            },
            TranscriptInputs = new[] { slotId },
            WorkflowPackage = workflowPackageRef,
        };
    }
}
