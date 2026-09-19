using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Consultologist.ZoomApp.Core.Zoom;

using Xunit;

namespace Consultologist.ZoomApp.Tests;

/// <summary>
/// The X-Zoom-App-Context decrypt, exercised by self-encrypting a context with
/// the layout Zoom documents ([ivLen:1][iv][aadLen:2 LE][aad][ctLen:4 LE][ct][tag:16],
/// AES-256-GCM, key = SHA-256(client secret)). The exact live-launch field
/// correspondence is confirmed during provisioning; this pins the codec.
/// </summary>
public class ZoomAppContextTests
{
    private const string ClientSecret = "test-zoom-client-secret";

    [Fact]
    public void TryDecrypt_RoundTripsUidAndMeeting()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        var header = Encrypt("""{"uid":"clinician-1","typ":"meeting","mid":"MEETING-UUID-1","exp":""" + exp + "}", ClientSecret);

        Assert.True(ZoomAppContext.TryDecrypt(header, ClientSecret, out var context));
        Assert.NotNull(context);
        Assert.Equal("clinician-1", context!.Uid);
        Assert.Equal("MEETING-UUID-1", context.Mid);
        Assert.False(context.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Context_IsExpired_WhenExpIsPast()
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        var header = Encrypt("""{"uid":"c","mid":"m","exp":""" + past + "}", ClientSecret);

        Assert.True(ZoomAppContext.TryDecrypt(header, ClientSecret, out var context));
        Assert.True(context!.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TryDecrypt_FailsClosed_OnWrongSecretGarbageOrMissingUid()
    {
        var header = Encrypt("""{"uid":"c","mid":"m"}""", ClientSecret);
        Assert.False(ZoomAppContext.TryDecrypt(header, "a-different-secret", out _));
        Assert.False(ZoomAppContext.TryDecrypt("not-base64url!!", ClientSecret, out _));
        Assert.False(ZoomAppContext.TryDecrypt(null, ClientSecret, out _));

        // Well-formed cipher whose payload has no uid is refused.
        var noUid = Encrypt("""{"typ":"meeting","mid":"m"}""", ClientSecret);
        Assert.False(ZoomAppContext.TryDecrypt(noUid, ClientSecret, out _));
    }

    private static string Encrypt(string json, string clientSecret)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret));
        var plaintext = Encoding.UTF8.GetBytes(json);
        var iv = RandomNumberGenerator.GetBytes(12);
        var aad = Array.Empty<byte>();
        var cipherText = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, tagSizeInBytes: 16))
        {
            gcm.Encrypt(iv, plaintext, cipherText, tag, aad);
        }

        using var raw = new MemoryStream();
        raw.WriteByte((byte)iv.Length);
        raw.Write(iv);
        var aadLen = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(aadLen, (ushort)aad.Length);
        raw.Write(aadLen);
        raw.Write(aad);
        var ctLen = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(ctLen, (uint)cipherText.Length);
        raw.Write(ctLen);
        raw.Write(cipherText);
        raw.Write(tag);

        return Convert.ToBase64String(raw.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
