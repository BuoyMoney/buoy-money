using System.Net;
using BuoyAI.API;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

public class RateLimitKeysTests
{
    private static DefaultHttpContext Context(string remote, string? forwardedFor)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        if (forwardedFor != null) ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return ctx;
    }

    [Fact]
    public void SelfHost_IgnoresASpoofedForwardedFor_SoVaryingItCannotMintNewBuckets()
    {
        var a = RateLimitKeys.ClientIp(Context("192.168.1.50", "203.0.113.1"), remoteAddressOnly: true);
        var b = RateLimitKeys.ClientIp(Context("192.168.1.50", "203.0.113.2"), remoteAddressOnly: true);

        a.Should().Be("192.168.1.50");
        b.Should().Be(a);
    }

    [Fact]
    public void Hosted_StillKeysOnTheOriginalClientBehindTheProxy()
    {
        RateLimitKeys.ClientIp(Context("127.0.0.1", "203.0.113.7, 10.0.0.2"), remoteAddressOnly: false)
            .Should().Be("203.0.113.7");
    }
}
