using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class ChatDisplaySurfaceTests
{
  [Theory]
  [InlineData("mobile-ios", true)]
  [InlineData("mobile-android", true)]
  [InlineData("MOBILE-IOS", true)]
  [InlineData("web", false)]
  [InlineData("desktop-macos", false)]
  [InlineData("", false)]
  [InlineData(null, false)]
  public void IsCompact_ClientType_MatchesOnlyMobile(string? clientType, bool expected)
  {
    ChatDisplaySurface.IsCompact(clientType).Should().Be(expected);
  }

  [Fact]
  public void Section_Compact_ForbidsTablesAndAsksForOneLinePerItem()
  {
    var section = ChatDisplaySurface.Section(compact: true);

    section.Should().Contain("PHONE");
    section.Should().Contain("NEVER use markdown tables");
    section.Should().Contain("one short line per item");
  }

  [Fact]
  public void Section_Wide_AllowsNarrowTables()
  {
    var section = ChatDisplaySurface.Section(compact: false);

    section.Should().Contain("WIDE SCREEN");
    section.Should().Contain("up to four columns");
    section.Should().NotContain("NEVER use markdown tables");
  }
}
