using System;
using System.IO;
using System.Text.Json;
using BuoyAI.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Data;

/// <summary>Keeps the copy-me template honest: a key in the wrong section silently binds to nothing.</summary>
public class AppSettingsExampleTests
{
  private static JsonElement LoadExample()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "BuoyAI.API", "appsettings.Example.json")))
      dir = dir.Parent;
    dir.Should().NotBeNull("the backend folder must be above the test output");
    var json = File.ReadAllText(Path.Combine(dir!.FullName, "src", "BuoyAI.API", "appsettings.Example.json"));
    return JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }).RootElement;
  }

  [Fact]
  public void Founding_SpotLimit_IsTopLevel_MatchingFoundingOptionsSection()
  {
    var root = LoadExample();
    root.TryGetProperty(FoundingOptions.SectionName, out var founding).Should().BeTrue();
    founding.GetProperty("SpotLimit").GetInt32().Should().Be(FoundingOptions.DefaultSpotLimit);
  }

  [Fact]
  public void GeminiModels_HasOnlyTierEntries()
  {
    var models = LoadExample().GetProperty("Gemini").GetProperty("Models");
    models.TryGetProperty("Founding", out _).Should().BeFalse();
    models.TryGetProperty("Admin", out var admin).Should().BeTrue();
    admin.TryGetProperty("Primary", out _).Should().BeTrue();
  }
}
