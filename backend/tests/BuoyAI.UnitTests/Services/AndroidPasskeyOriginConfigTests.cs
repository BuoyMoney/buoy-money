using System.Text.Json;
using BuoyAI.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Native Android passkeys sign the ceremony with an origin of "android:apk-key-hash:&lt;base64url sha256 of the
/// app signing cert&gt;". That origin must be in WebAuthn:AllowedOrigins in prod and staging, and the same
/// cert must be listed in the assetlinks.json the marketing site publishes, or Credential Manager refuses
/// to create the passkey. This keeps the three in step.
/// </summary>
public class AndroidPasskeyOriginConfigTests
{
  /// <summary>Skips when the hosted appsettings files are absent (the public export omits them).</summary>
  private sealed class HostedConfigTheoryAttribute : TheoryAttribute
  {
    public HostedConfigTheoryAttribute()
    {
      var api = Path.Combine(FindRepoRoot(), "backend", "src", "BuoyAI.API");
      if (!File.Exists(Path.Combine(api, "appsettings.Production.json")) || !File.Exists(Path.Combine(api, "appsettings.Staging.json")))
      {
        Skip = "appsettings.Production.json / appsettings.Staging.json are not in this checkout.";
      }
    }
  }

  [HostedConfigTheory]
  [InlineData("appsettings.Production.json")]
  [InlineData("appsettings.Staging.json")]
  public void HostedConfig_AllowsTheAndroidAppOrigin_ThatMatchesAssetlinks(string file)
  {
    var root = FindRepoRoot();
    using var settingsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "backend", "src", "BuoyAI.API", file)));
    var webAuthn = settingsDoc.RootElement.GetProperty("WebAuthn");
    var settings = new WebAuthnSettings
    {
      Origin = webAuthn.GetProperty("Origin").GetString()!,
      AllowedOrigins = webAuthn.GetProperty("AllowedOrigins").EnumerateArray().Select(e => e.GetString()!).ToList(),
    };

    using var links = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "marketing", "public", ".well-known", "assetlinks.json")));
    var target = links.RootElement.EnumerateArray().Single().GetProperty("target");
    target.GetProperty("package_name").GetString().Should().Be("com.buoyai.buoy_ai");

    foreach (var fingerprint in target.GetProperty("sha256_cert_fingerprints").EnumerateArray().Select(e => e.GetString()!))
    {
      var hash = Convert.FromHexString(fingerprint.Replace(":", ""));
      hash.Length.Should().Be(32);
      var origin = "android:apk-key-hash:" + Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      settings.IsAllowedOrigin(origin).Should().BeTrue($"{file} must allow the origin for {fingerprint}");
    }

    settings.IsAllowedOrigin("https://evil.example").Should().BeFalse();
    settings.IsAllowedOrigin("android:apk-key-hash:notours").Should().BeFalse();
  }

  private static string FindRepoRoot()
  {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "marketing", "public")))
    {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root (with marketing/public) not found above the test output.");
  }
}
