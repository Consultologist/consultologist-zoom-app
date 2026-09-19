using Consultologist.ZoomApp.Core.Storage;
using Consultologist.ZoomApp.Storage;
using Consultologist.ZoomApp.Zoom;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Consultologist.ZoomApp.Tests;

public class TableKeysTests
{
    [Theory]
    [InlineData("simple-guid-1234")]
    [InlineData("/abc==")]              // a leading '/' (illegal in a table key)
    [InlineData("uuid//with//slashes")] // Zoom double-slash UUID
    public void Encode_ProducesLegalTableKeyChars_AndIsStable(string value)
    {
        var key = TableKeys.Encode(value);

        Assert.Matches("^[A-Za-z0-9_-]+$", key);          // no '/', '\\', '#', '?', control chars
        Assert.Equal(key, TableKeys.Encode(value));       // deterministic — lookups re-encode
    }

    [Fact]
    public void Encode_IsDistinctPerValue()
    {
        Assert.NotEqual(TableKeys.Encode("a"), TableKeys.Encode("b"));
    }
}

public class ProtectedTokenCodecTests
{
    private static IDataProtector Protector()
    {
        var provider = new ServiceCollection().AddDataProtection().Services
            .BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        return provider.CreateProtector("test");
    }

    [Fact]
    public void Encode_ThenDecode_RoundTripsTheTokenSet_AndIsNotPlaintext()
    {
        var protector = Protector();
        var set = new ZoomTokenSet("access-abc", "refresh-xyz", DateTimeOffset.UnixEpoch.AddHours(1));

        var encoded = protector is { } p ? ProtectedTokenCodec.Encode(p, set) : throw new InvalidOperationException();
        Assert.DoesNotContain("refresh-xyz", encoded);    // encrypted at rest
        Assert.DoesNotContain("access-abc", encoded);

        var decoded = ProtectedTokenCodec.Decode(protector, encoded);
        Assert.Equal(set, decoded);
    }

    [Fact]
    public void Decode_FailsClosed_OnGarbage()
    {
        Assert.Null(ProtectedTokenCodec.Decode(Protector(), "not-a-protected-value"));
    }
}
