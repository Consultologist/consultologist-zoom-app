using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Consultologist.ZoomApp.Zoom;

/// <summary>The decrypted <c>X-Zoom-App-Context</c> — who opened the panel and,
/// in a meeting, which meeting. Only the fields the app uses are modelled.</summary>
public sealed record ZoomContext(string Uid, string? Typ, long? Ts, long? Exp, string? Mid)
{
    /// <summary>Zoom requires callers to check <c>exp</c> before trusting the context.</summary>
    public bool IsExpired(DateTimeOffset now) =>
        Exp is { } e && DateTimeOffset.FromUnixTimeMilliseconds(e) <= now;
}

/// <summary>
/// Decrypts the encrypted <c>X-Zoom-App-Context</c> header Zoom sends to the Home
/// URL. Layout after base64url-decode (per Zoom's spec):
/// <c>[ivLen:1][iv][aadLen:2 LE][aad][ctLen:4 LE][ct][tag:16]</c>; AES-256-GCM
/// with the key = SHA-256(client secret). Validate <c>exp</c> before use.
/// </summary>
/// <remarks>Verify end-to-end against a live Zoom launch during provisioning —
/// the layout is implemented from the published spec but is untested here.</remarks>
public static class ZoomAppContext
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static bool TryDecrypt(string? header, string clientSecret, out ZoomContext? context)
    {
        context = null;
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(clientSecret))
        {
            return false;
        }

        try
        {
            var raw = Base64UrlDecode(header);
            var offset = 0;

            var ivLen = raw[offset];
            offset += 1;
            var iv = raw.AsSpan(offset, ivLen).ToArray();
            offset += ivLen;

            var aadLen = BitConverter.ToUInt16(raw, offset); // little-endian
            offset += 2;
            var aad = raw.AsSpan(offset, aadLen).ToArray();
            offset += aadLen;

            var ctLen = (int)BitConverter.ToUInt32(raw, offset); // little-endian
            offset += 4;
            var cipherText = raw.AsSpan(offset, ctLen).ToArray();
            offset += ctLen;

            var tag = raw.AsSpan(offset, 16).ToArray();

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret));
            var plaintext = new byte[ctLen];
            using (var gcm = new AesGcm(key, tagSizeInBytes: 16))
            {
                gcm.Decrypt(iv, cipherText, tag, plaintext, aad);
            }

            var json = JsonSerializer.Deserialize<ContextJson>(plaintext, WebJson);
            if (json?.Uid is null)
            {
                return false;
            }

            context = new ZoomContext(json.Uid, json.Typ, json.Ts, json.Exp, json.Mid);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or CryptographicException or JsonException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
        return Convert.FromBase64String(s);
    }

    private sealed record ContextJson(string? Uid, string? Typ, long? Ts, long? Exp, string? Mid);
}
