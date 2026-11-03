using System;
using System.Linq;
using BuoyAI.Core.Entities;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The single rule every AI path uses (request gate, background jobs, embeddings, MCP).
/// App Store 5.1.1(i)/5.1.2(i): explicit, current consent or nothing goes to Gemini.
/// </summary>
public class AiConsentTests
{
    private static User U(DateTime? granted, DateTime? withdrawn = null, int? version = AiConsent.CurrentVersion) => new()
    {
        Id = 1, Email = "u@example.com", PasswordHash = "h",
        AiConsentGrantedAt = granted, AiConsentWithdrawnAt = withdrawn, AiConsentVersion = version,
    };

    [Fact]
    public void IsCurrent_GrantedAtTheCurrentVersion_IsTrue() =>
        AiConsent.IsCurrent(U(DateTime.UtcNow)).Should().BeTrue();

    [Fact]
    public void IsCurrent_NeverDecided_IsFalse() =>
        AiConsent.IsCurrent(U(null, version: null)).Should().BeFalse();

    [Fact]
    public void IsCurrent_GrantFromBeforeVersioning_IsFalse() =>
        AiConsent.IsCurrent(U(DateTime.UtcNow.AddDays(-10), version: null)).Should().BeFalse();

    [Fact]
    public void IsCurrent_GrantUnderAnOlderVersion_IsFalse() =>
        AiConsent.IsCurrent(U(DateTime.UtcNow, version: AiConsent.CurrentVersion - 1)).Should().BeFalse();

    [Fact]
    public void IsCurrent_WithdrawnAfterGranting_IsFalse() =>
        AiConsent.IsCurrent(U(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1))).Should().BeFalse();

    [Fact]
    public void IsCurrent_RegrantedAfterWithdrawing_IsTrue() =>
        AiConsent.IsCurrent(U(DateTime.UtcNow, DateTime.UtcNow.AddDays(-1))).Should().BeTrue();

    [Fact]
    public void IsCurrent_NoUser_IsFalse() => AiConsent.IsCurrent(null).Should().BeFalse();

    [Fact]
    public void VisibleGrantedAt_HidesAStaleGrant_SoOlderClientsAskAgain()
    {
        AiConsent.VisibleGrantedAt(U(DateTime.UtcNow.AddDays(-10), version: null)).Should().BeNull();
        var granted = DateTime.UtcNow;
        AiConsent.VisibleGrantedAt(U(granted)).Should().Be(granted);
    }

    [Fact]
    public void IsCurrentExpr_MatchesIsCurrent_WhenUsedInAQuery()
    {
        var users = new[]
        {
            U(DateTime.UtcNow),
            U(DateTime.UtcNow, version: null),
            U(null, version: null),
            U(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1)),
        };

        users.AsQueryable().Where(AiConsent.IsCurrentExpr).Should().HaveCount(1);
    }
}
