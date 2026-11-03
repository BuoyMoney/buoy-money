using System.Linq;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Each app store gets its own reviewer family. Per-account state (AI consent above all)
/// follows the login, so a grant by the Play reviewer on Android must not reach Apple's
/// reviewer on iPad.
/// </summary>
public class ReviewerProfilesTests
{
    [Fact]
    public void Profiles_GiveEachStoreItsOwnLogins()
    {
        ReviewerAccountSeeder.Play.Email.Should().Be("play-reviewer@buoy-ai.com");
        ReviewerAccountSeeder.Apple.Email.Should().Be("apple-reviewer@buoy-ai.com");

        var emails = ReviewerAccountSeeder.Profiles.SelectMany(p => new[] { p.Email, p.ChildEmail }).ToList();
        emails.Should().OnlyHaveUniqueItems();
        ReviewerAccountSeeder.Profiles.Select(p => p.Username)
            .Concat(ReviewerAccountSeeder.Profiles.Select(p => p.ChildUsername))
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AllEmails_CoversEveryReviewerLogin_SoTheDevWipeKeepsThem()
    {
        ReviewerAccountSeeder.AllEmails.Should().BeEquivalentTo(
            "play-reviewer@buoy-ai.com", "alex-child@buoy-ai.com",
            "apple-reviewer@buoy-ai.com", "jamie-child@buoy-ai.com");
    }
}
