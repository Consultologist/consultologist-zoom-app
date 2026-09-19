using Consultologist.ZoomApp.Core.Security;

using Xunit;

namespace Consultologist.ZoomApp.Tests;

public class CspPolicyTests
{
    [Fact]
    public void Build_FramesZoomOnly_AndAllowsTheSdkCdn_ByDefault()
    {
        var csp = CspPolicy.Build(new CspOptions());

        Assert.Contains("frame-ancestors https://zoom.us https://*.zoom.us https://*.zoomgov.com", csp);
        Assert.Contains("script-src 'self' https://cdn.jsdelivr.net", csp);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        // Backend-mediated: no engine origin is granted to the browser.
        Assert.DoesNotContain("consultologist.ai", csp);
    }

    [Fact]
    public void Build_HonoursConfiguredOrigins()
    {
        var csp = CspPolicy.Build(new CspOptions
        {
            FrameAncestors = new[] { "https://example.zoom.us" },
            ScriptSrc = new[] { "'self'" },
        });

        Assert.Contains("frame-ancestors https://example.zoom.us", csp);
        Assert.Contains("script-src 'self';", csp + ";");
        Assert.DoesNotContain("cdn.jsdelivr.net", csp);
    }
}
