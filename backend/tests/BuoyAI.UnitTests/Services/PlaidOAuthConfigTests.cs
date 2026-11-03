using System.Collections.Generic;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Going.Plaid.Link;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// OAuth banks (Chase and the other big ones) need a way back into the app after the bank's
/// own login. The same rules now apply to fresh connects and to update-mode (reconnect)
/// tokens, which used to get none.
/// </summary>
public class PlaidOAuthConfigTests
{
    private static IConfiguration Config(string? iosRedirect = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plaid:AndroidPackageName"] = "com.buoyai.buoy_ai",
            ["Plaid:IosRedirectUri"] = iosRedirect,
        }).Build();

    [Fact]
    public void ApplyPlatformOAuth_Android_SetsThePackageName()
    {
        var request = new LinkTokenCreateRequest();

        PlaidService.ApplyPlatformOAuth(request, "android", Config());

        request.AndroidPackageName.Should().Be("com.buoyai.buoy_ai");
        request.RedirectUri.Should().BeNull();
    }

    [Fact]
    public void ApplyPlatformOAuth_Ios_WithoutAConfiguredUri_LeavesTheRequestAlone()
    {
        // An unregistered redirect_uri makes Plaid reject the whole token, so an unset key must
        // not send one.
        var request = new LinkTokenCreateRequest();

        PlaidService.ApplyPlatformOAuth(request, "ios", Config());

        request.RedirectUri.Should().BeNull();
        request.AndroidPackageName.Should().BeNull();
    }

    [Fact]
    public void ApplyPlatformOAuth_Ios_WithAConfiguredUri_SetsTheRedirect()
    {
        var request = new LinkTokenCreateRequest();

        PlaidService.ApplyPlatformOAuth(request, "ios", Config("https://buoy-ai.com/app/plaid-oauth"));

        request.RedirectUri.Should().Be("https://buoy-ai.com/app/plaid-oauth");
        request.AndroidPackageName.Should().BeNull();
    }

    [Theory]
    [InlineData("web")]
    [InlineData(null)]
    [InlineData("other")]
    public void ApplyPlatformOAuth_WebOrUnknown_SetsNothing(string? platform)
    {
        var request = new LinkTokenCreateRequest();

        PlaidService.ApplyPlatformOAuth(request, platform, Config("https://buoy-ai.com/app/plaid-oauth"));

        request.RedirectUri.Should().BeNull();
        request.AndroidPackageName.Should().BeNull();
    }
}
