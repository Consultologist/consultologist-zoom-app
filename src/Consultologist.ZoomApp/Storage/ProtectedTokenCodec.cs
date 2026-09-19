using System.Security.Cryptography;
using System.Text.Json;

using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.DataProtection;

namespace Consultologist.ZoomApp.Storage;

/// <summary>
/// Encrypts a <see cref="ZoomTokenSet"/> for storage. The refresh token is
/// sensitive, so the token JSON is Data-Protection-encrypted before it is written
/// to Table storage and decrypted on read. Pure over an <see cref="IDataProtector"/>.
/// </summary>
public static class ProtectedTokenCodec
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Encode(IDataProtector protector, ZoomTokenSet set) =>
        Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(set, Json)));

    public static ZoomTokenSet? Decode(IDataProtector protector, string protectedValue)
    {
        try
        {
            return JsonSerializer.Deserialize<ZoomTokenSet>(protector.Unprotect(Convert.FromBase64String(protectedValue)), Json);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }
}
