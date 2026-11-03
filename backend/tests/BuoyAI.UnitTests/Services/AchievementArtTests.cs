using BuoyAI.Core.DTOs.Gamification;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The app draws each badge from flutter/assets/achievements/&lt;key&gt;.png and quietly falls back
/// to the emoji when the file is missing. Chronic Amnesia and Time Lord shipped that way: added to
/// the catalog, never given art (scripts/badges/ makes it). This fails when a key has no art.
/// </summary>
public class AchievementArtTests
{
  [Fact]
  public void EveryAchievement_HasBadgeArt()
  {
    var assets = Path.Combine(FindRepoRoot(), "flutter", "assets", "achievements");

    var missing = AchievementDefinitions.All.Keys
      .Where(key => !File.Exists(Path.Combine(assets, $"{key}.png")))
      .ToList();

    missing.Should().BeEmpty(
      "every badge needs art: add it to BADGES in scripts/badges/generate_badges.py, then run the generate and circle_cut steps in scripts/badges/README.md");
  }

  private static string FindRepoRoot()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "flutter", "assets")))
    {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root (with flutter/assets) not found above the test output.");
  }
}
