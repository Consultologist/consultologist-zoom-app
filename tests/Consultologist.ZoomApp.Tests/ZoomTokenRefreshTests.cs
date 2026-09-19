using System.Net;
using System.Net.Http.Json;

using Consultologist.ZoomApp.Core.Zoom;
using Consultologist.ZoomApp.Zoom;

using Microsoft.Extensions.Options;

using Xunit;

namespace Consultologist.ZoomApp.Tests;

public class ZoomTokenExpiryTests
{
    [Fact]
    public void NeedsRefresh_WhenAtOrPastExpiryMinusSkew()
    {
        var now = DateTimeOffset.UnixEpoch;
        Assert.True(ZoomTokenExpiry.NeedsRefresh(now.AddMinutes(1), now));   // inside the 2-min skew
        Assert.True(ZoomTokenExpiry.NeedsRefresh(now.AddMinutes(-5), now));  // already expired
        Assert.False(ZoomTokenExpiry.NeedsRefresh(now.AddMinutes(30), now)); // plenty of life
    }
}

public class ZoomTokenProviderTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ZoomClient ZoomWith(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new ZoomOptions { ClientId = "c", ClientSecret = "s" }));

    [Fact]
    public async Task GetValidToken_RefreshesAndPersists_WhenNearExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClinicianZoomTokens();
        await store.StoreAsync("clin", new ZoomTokenSet("old", "refresh-1", now.AddMinutes(1)));

        var handler = new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { access_token = "fresh", refresh_token = "refresh-2", expires_in = 3600 }),
        });
        var provider = new ZoomTokenProvider(store, ZoomWith(handler), new FixedClock(now));

        var token = await provider.GetValidTokenAsync("clin");

        Assert.Equal(1, handler.Calls);
        Assert.Equal("fresh", token!.AccessToken);
        Assert.Equal("refresh-2", token.RefreshToken);
        Assert.Equal("fresh", (await store.GetAsync("clin"))!.AccessToken); // persisted
    }

    [Fact]
    public async Task GetValidToken_ReturnsStored_WithoutCallingZoom_WhenFresh()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryClinicianZoomTokens();
        await store.StoreAsync("clin", new ZoomTokenSet("live", "refresh-1", now.AddMinutes(30)));

        var handler = new StubHandler(() => throw new InvalidOperationException("should not refresh"));
        var provider = new ZoomTokenProvider(store, ZoomWith(handler), new FixedClock(now));

        var token = await provider.GetValidTokenAsync("clin");

        Assert.Equal(0, handler.Calls);
        Assert.Equal("live", token!.AccessToken);
    }

    [Fact]
    public async Task GetValidToken_ReturnsNull_WhenNotConnected()
    {
        var provider = new ZoomTokenProvider(
            new InMemoryClinicianZoomTokens(),
            ZoomWith(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK))),
            new FixedClock(DateTimeOffset.UtcNow));

        Assert.Null(await provider.GetValidTokenAsync("nobody"));
    }
}
