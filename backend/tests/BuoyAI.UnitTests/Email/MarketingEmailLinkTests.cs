using FluentAssertions;
using Xunit;
using BuoyAI.Infrastructure.Email;

namespace BuoyAI.UnitTests.Email;

/// <summary>
/// Every link in a lead email has to point at the environment that sent it.
///
/// These emails previously hardcoded https://buoy-ai.com, so a message sent from staging
/// carried an unsubscribe link to production, where the token means nothing. Clicking it did
/// nothing at all. Nothing in the existing suite looked at the rendered links, so the whole
/// class of bug was invisible.
/// </summary>
public class MarketingEmailLinkTests
{
    private const string Staging = "https://staging.buoy-ai.com";

    [Fact]
    public void Unsubscribe_url_is_built_on_the_supplied_host()
    {
        MarketingEmailTemplates.UnsubscribeUrl(Staging, "abc123")
            .Should().Be("https://staging.buoy-ai.com/unsubscribe?t=abc123");
    }

    [Fact]
    public void A_trailing_slash_on_the_configured_base_does_not_double_up()
    {
        // App:BaseUrl is hand-edited per environment, so a stray slash is a matter of time.
        MarketingEmailTemplates.UnsubscribeUrl("https://staging.buoy-ai.com/", "abc123")
            .Should().NotContain("//unsubscribe");
    }

    [Fact]
    public void The_token_is_url_escaped()
    {
        MarketingEmailTemplates.UnsubscribeUrl(Staging, "a+b/c=")
            .Should().EndWith("?t=a%2Bb%2Fc%3D");
    }

    [Fact]
    public void The_offer_email_never_leaks_a_link_to_a_different_environment()
    {
        var url = MarketingEmailTemplates.UnsubscribeUrl(Staging, "tok");
        var (html, text) = MarketingEmailTemplates.ExtendedTrialOffer(
            30, 14, $"{Staging}/app/register", null, url);

        foreach (var body in new[] { html, text })
        {
            body.Should().Contain(url);
            // "https://buoy-ai.com" must not appear as a link host. staging.buoy-ai.com is
            // fine, hence matching on the scheme too.
            body.Should().NotContain("https://buoy-ai.com");
        }
    }

    [Fact]
    public void The_existing_account_email_carries_the_same_working_opt_out()
    {
        var url = MarketingEmailTemplates.UnsubscribeUrl(Staging, "tok");
        var (html, text) = MarketingEmailTemplates.ExistingAccountOffer(
            $"{Staging}/app/login", null, url);

        html.Should().Contain(url);
        text.Should().Contain(url);
        html.Should().NotContain("https://buoy-ai.com");
    }

    [Fact]
    public void The_offer_email_says_a_month_instead_of_two_weeks_in_the_first_person()
    {
        var (html, text) = MarketingEmailTemplates.ExtendedTrialOffer(
            30, 14, $"{Staging}/app/register", null, "https://x/u");

        foreach (var body in new[] { html, text })
        {
            body.Should().Contain("a month");
            body.Should().Contain("two weeks");
            body.Should().NotMatchRegex(@"b60b");
            body.Should().NotContain("We'll");
        }
    }

    [Fact]
    public void The_reminder_never_states_a_day_count()
    {
        var (html, text) = MarketingEmailTemplates.ExtendedTrialReminder($"{Staging}/app/register", "https://x/u");

        foreach (var body in new[] { html, text })
        {
            body.Should().NotMatchRegex(@"b60b");
            body.Should().NotContain("30 days");
        }
    }
}
