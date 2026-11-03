using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.UnitTests.Services;

namespace BuoyAI.UnitTests.Utilities;

/// <summary>
/// The operator allowlist must grant ACCESS, not just the label the profile shows.
///
/// It used to do only the latter: the profile DTO rewrote the displayed tier while every
/// server-side gate compared <c>User.SubscriptionTier</c> straight from the database. An
/// operator therefore saw "Admin" in their profile and got a 403 creating a family — the app
/// contradicting itself about its own state.
/// </summary>
public class AdminPolicyAccessTests
{
    private static IConfiguration ConfigWithAdmins(params string[] emails)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < emails.Length; i++)
        {
            values[$"Admin:Emails:{i}"] = emails[i];
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static User UserWith(string email, SubscriptionTier? tier = null,
                                 bool trialActive = false, DateTime? trialEnd = null) =>
        new() { Email = email, SubscriptionTier = tier, IsTrialActive = trialActive, TrialEndDate = trialEnd };

    [Fact]
    public void HasPremiumAccess_AllowlistedOperator_IsEntitledEvenWithNoTierAtAll()
    {
        // Exactly the production state that caused the 403: allowlisted, tier NULL,
        // trial flag stale against a date long past.
        var config = ConfigWithAdmins("operator@example.com");
        var user = UserWith("operator@example.com", tier: null,
                            trialActive: true, trialEnd: DateTime.UtcNow.AddDays(-45));

        TestEntitlements.For(config).HasPremiumAccess(user).Should().BeTrue();
    }

    [Fact]
    public void HasPremiumAccess_AllowlistIsCaseAndWhitespaceInsensitive()
    {
        var config = ConfigWithAdmins("  Operator@Example.COM  ");
        TestEntitlements.For(config).HasPremiumAccess(UserWith("operator@example.com")).Should().BeTrue();
    }

    [Fact]
    public void HasPremiumAccess_OrdinaryUserWithNoTier_IsNotEntitled()
    {
        // The allowlist must not become a way to hand out Premium generally.
        var config = ConfigWithAdmins("operator@example.com");
        TestEntitlements.For(config).HasPremiumAccess(UserWith("someone@example.com")).Should().BeFalse();
    }

    [Fact]
    public void HasPremiumAccess_LapsedTrialWithNoTier_IsNotEntitled()
    {
        var config = ConfigWithAdmins();
        var user = UserWith("lapsed@example.com", trialActive: true,
                            trialEnd: DateTime.UtcNow.AddDays(-1));

        // A stale IsTrialActive flag must not survive its own end date.
        TestEntitlements.For(config).HasPremiumAccess(user).Should().BeFalse();
    }

    [Fact]
    public void HasPremiumAccess_RunningTrialOrPaidTier_IsEntitled()
    {
        var config = ConfigWithAdmins();

        TestEntitlements.For(config).HasPremiumAccess(
            UserWith("t@example.com", trialActive: true, trialEnd: DateTime.UtcNow.AddDays(5)))
            .Should().BeTrue();

        TestEntitlements.For(config).HasPremiumAccess(
            UserWith("p@example.com", tier: SubscriptionTier.Premium)).Should().BeTrue();

        TestEntitlements.For(config).HasPremiumAccess(
            UserWith("i@example.com", tier: SubscriptionTier.PremiumInherited)).Should().BeTrue();

        TestEntitlements.For(config).HasPremiumAccess(
            UserWith("s@example.com", tier: SubscriptionTier.Standard)).Should().BeFalse();
    }
}
