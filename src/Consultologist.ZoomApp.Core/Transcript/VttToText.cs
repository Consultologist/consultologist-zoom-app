using System.Text;
using System.Text.RegularExpressions;

namespace Consultologist.ZoomApp.Core.Transcript;

/// <summary>
/// Turns a Zoom speaker-labeled WEBVTT transcript into plain, speaker-labeled
/// text. The engine stamps the <c>transcript</c> origin only on a slot filled
/// from a <em>file</em> (its <c>ExtractInputFilesAsync</c> path), so the app
/// submits the transcript as a <c>text/plain</c> document rather than as typed
/// text — which would carry no origin. Reducing the VTT to plain text here (a)
/// keeps the submission robust to whatever extractors the engine has, and (b)
/// drops cue timings/ids the consult does not need while preserving who said
/// what. A later build pass may switch to submitting the raw VTT bytes if the
/// engine gains a <c>.vtt</c> extractor.
/// </summary>
public static class VttToText
{
    // A cue's timing line: "00:00:01.000 --> 00:00:04.000 [settings]".
    private const string Arrow = "-->";

    // "<v Speaker Name>text..." — Zoom sometimes voice-tags the speaker.
    private static readonly Regex VoiceOpen = new(@"^<v\s+([^>]+)>", RegexOptions.Compiled);

    // Any remaining tag: "<...>" / "</...>".
    private static readonly Regex AnyTag = new(@"</?[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// The transcript as plain text — one line per cue, each "Speaker: words"
    /// where the VTT carried a speaker, joined by newlines. Blank input, a bare
    /// <c>WEBVTT</c> header, NOTE/STYLE blocks and cue identifiers all yield no
    /// lines. Never returns null.
    /// </summary>
    public static string ToText(string? vtt)
    {
        if (string.IsNullOrWhiteSpace(vtt))
        {
            return string.Empty;
        }

        // Blocks are separated by one or more blank lines. A block is a cue iff
        // one of its lines is a timing line (contains "-->"); everything else
        // (the WEBVTT header, NOTE, STYLE, REGION) is skipped.
        var lines = vtt.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>();
        var block = new List<string>();

        void Flush()
        {
            if (block.Count > 0)
            {
                var cue = ReadCue(block);
                if (!string.IsNullOrWhiteSpace(cue))
                {
                    output.Add(cue);
                }

                block.Clear();
            }
        }

        foreach (var line in lines)
        {
            if (line.Trim().Length == 0)
            {
                Flush();
            }
            else
            {
                block.Add(line);
            }
        }

        Flush();
        return string.Join("\n", output);
    }

    // A cue block: an optional identifier line, a timing line (with "-->"), then
    // one or more payload lines. Blocks with no timing line are not cues.
    private static string? ReadCue(IReadOnlyList<string> block)
    {
        var timing = -1;
        for (var i = 0; i < block.Count; i++)
        {
            if (block[i].Contains(Arrow, StringComparison.Ordinal))
            {
                timing = i;
                break;
            }
        }

        if (timing < 0)
        {
            return null;
        }

        var payload = new StringBuilder();
        for (var i = timing + 1; i < block.Count; i++)
        {
            var text = CleanTags(block[i].Trim());
            if (text.Length == 0)
            {
                continue;
            }

            if (payload.Length > 0)
            {
                payload.Append(' ');
            }

            payload.Append(text);
        }

        return payload.Length == 0 ? null : payload.ToString();
    }

    // "<v Speaker>words" -> "Speaker: words"; strip any other tags.
    private static string CleanTags(string line)
    {
        var voiced = VoiceOpen.Replace(line, m => m.Groups[1].Value.Trim() + ": ");
        return AnyTag.Replace(voiced, string.Empty).Trim();
    }
}
