using BuoyAI.Core.Utilities;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Utilities;

public class TrialEmailHasherTests
{
  [Theory]
  [InlineData("user@example.com")]
  [InlineData("  User@Example.COM  ")]
  [InlineData("USER@EXAMPLE.COM")]
  public void Compute_NormalizesCaseAndWhitespace_ToSameHash(string variant)
  {
    var canonical = TrialEmailHasher.Compute("user@example.com");
    TrialEmailHasher.Compute(variant).Should().Be(canonical);
  }

  [Fact]
  public void Compute_ProducesLowercaseHex64_Sha256()
  {
    var hash = TrialEmailHasher.Compute("user@example.com");
    hash.Should().NotBeNull();
    hash!.Length.Should().Be(64);
    hash.Should().MatchRegex("^[0-9a-f]{64}$");
  }

  [Fact]
  public void Compute_DifferentEmails_ProduceDifferentHashes()
  {
    TrialEmailHasher.Compute("a@example.com")
      .Should().NotBe(TrialEmailHasher.Compute("b@example.com"));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  public void Compute_NullOrWhitespace_ReturnsNull(string? input)
  {
    TrialEmailHasher.Compute(input).Should().BeNull();
  }
}
