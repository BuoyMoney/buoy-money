using Xunit;
using FluentAssertions;
using BuoyAI.Core.Utilities;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Staging and production share one live Stripe account, so these rules are the only thing
/// stopping a staging account deletion from cancelling a real production subscription for
/// the same email address. The asymmetry is deliberate throughout: when ownership is
/// uncertain we decline to act, because orphaning a staging customer is recoverable and
/// deleting a live production customer is not.
/// </summary>
public class StripeEnvironmentPolicyTests
{
    private static Dictionary<string, string> Tagged(string environment) =>
        new() { ["environment"] = environment, ["user_id"] = "42" };

    [Theory]
    [InlineData("Production", "production")]
    [InlineData("Staging", "staging")]
    [InlineData("Development", "development")]
    [InlineData("  Staging  ", "staging")]
    public void Tag_NormalizesEnvironmentName(string input, string expected)
    {
        StripeEnvironmentPolicy.Tag(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tag_BlankEnvironment_IsNotProduction(string? input)
    {
        // A misconfigured host must fail closed. If a blank name resolved to "production"
        // it would inherit production's right to claim and delete untagged customers.
        var tag = StripeEnvironmentPolicy.Tag(input);

        tag.Should().NotBe(StripeEnvironmentPolicy.ProductionTag);
        StripeEnvironmentPolicy.IsOwnedBy(null, tag).Should().BeFalse();
    }

    [Fact]
    public void IsOwnedBy_MatchingTag_IsOwned()
    {
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("staging"), "staging").Should().BeTrue();
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("production"), "production").Should().BeTrue();
    }

    [Fact]
    public void IsOwnedBy_ProductionCustomer_IsNeverOwnedByStaging()
    {
        // The bug this whole policy exists to prevent.
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("production"), "staging").Should().BeFalse();
    }

    [Fact]
    public void IsOwnedBy_StagingCustomer_IsNotOwnedByProduction()
    {
        // The reverse direction matters too: production must not adopt a customer staging
        // created, or a staging cleanup would take production's subscription with it.
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("staging"), "production").Should().BeFalse();
    }

    [Fact]
    public void IsOwnedBy_UntaggedCustomer_BelongsToProductionOnly()
    {
        // Everything predating this policy was created by production or by hand in the
        // dashboard, so production may still claim it and no one else may.
        var legacy = new Dictionary<string, string> { ["user_id"] = "42" };

        StripeEnvironmentPolicy.IsOwnedBy(legacy, "production").Should().BeTrue();
        StripeEnvironmentPolicy.IsOwnedBy(legacy, "staging").Should().BeFalse();
        StripeEnvironmentPolicy.IsOwnedBy(null, "staging").Should().BeFalse();
    }

    [Fact]
    public void IsOwnedBy_BlankTagValue_TreatedAsUntagged()
    {
        var blank = new Dictionary<string, string> { ["environment"] = "   " };

        StripeEnvironmentPolicy.IsOwnedBy(blank, "production").Should().BeTrue();
        StripeEnvironmentPolicy.IsOwnedBy(blank, "staging").Should().BeFalse();
    }

    [Fact]
    public void IsOwnedBy_ComparisonIsCaseInsensitive()
    {
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("Staging"), "staging").Should().BeTrue();
        StripeEnvironmentPolicy.IsOwnedBy(Tagged("staging"), "STAGING").Should().BeTrue();
    }

    [Fact]
    public void Owner_ReportsTheCreatingEnvironment()
    {
        StripeEnvironmentPolicy.Owner(Tagged("Production")).Should().Be("production");
        StripeEnvironmentPolicy.Owner(new Dictionary<string, string>()).Should().BeNull();
        StripeEnvironmentPolicy.Owner(null).Should().BeNull();
    }
}
