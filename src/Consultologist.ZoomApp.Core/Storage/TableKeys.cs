using System.Text;

namespace Consultologist.ZoomApp.Core.Storage;

/// <summary>
/// Azure Table partition/row keys forbid '/', '\\', '#', '?', control chars and
/// have length limits; a Zoom meeting UUID can contain '/' (and '//'). Base64url
/// of the UTF-8 value is always a legal key and needs no decoding — lookups
/// re-encode the same value.
/// </summary>
public static class TableKeys
{
    public static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
