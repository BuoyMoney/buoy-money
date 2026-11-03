using BuoyAI.Infrastructure.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuoyAI.UnitTests.Utilities;

/// <summary>
/// The admin allowlist used to sit in the tracked appsettings.Production.json. The file is not
/// published, but the address still should not be in git, so hosted gets it from the Admin__Emails__0
/// environment variable that the deploy workflow sets from the ADMIN_EMAIL secret.
/// </summary>
public class AdminEmailDeployConfigTests
{
    // The public repo does not ship appsettings.Production.json (or scripts/publish/denylist.txt, which marks
    // the private repo). In the public tree there is nothing to check; in the private tree a missing file is a failure.
    private static string? ProductionSettingsPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "backend", "src", "BuoyAI.API", "appsettings.Production.json");
            if (File.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(dir, "scripts", "publish", "denylist.txt")))
                throw new FileNotFoundException("Private tree found but appsettings.Production.json is missing", candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    [Fact]
    public void DeployWorkflow_PassesTheAdminEmailSecret()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var wf = Path.Combine(dir, ".github", "workflows", "deploy-app.yml");
            if (File.Exists(wf))
            {
                File.ReadAllText(wf).Should().Contain("Admin__Emails__0");
                return;
            }
            dir = Path.GetDirectoryName(dir);
        }
        // Public tree: deploy workflows are not published.
    }

    [Fact]
    public void ProductionSettingsFile_NoLongerNamesAnyAdmin()
    {
        var path = ProductionSettingsPath();
        if (path == null) return;
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        config.GetSection("Admin:Emails").Get<string[]>().Should().BeNullOrEmpty();
        File.ReadAllText(path).Should().NotContain("@gmail.com");
    }

    [Fact]
    public void EnvironmentVariable_OverridesTheEmptyList_SoTheOperatorStaysAdmin()
    {
        // Same shape the deploy sets: Admin__Emails__0 becomes Admin:Emails:0 on top of the file.
        var path = ProductionSettingsPath();
        if (path == null) return;
        var config = new ConfigurationBuilder()
            .AddJsonFile(path)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Emails:0"] = "Owner@Example.com" })
            .Build();

        AdminPolicy.IsAdminEmail(config, "owner@example.com").Should().BeTrue();
        AdminPolicy.IsAdminEmail(config, "someone@example.com").Should().BeFalse();
    }

    [Fact]
    public void WithoutTheVariable_NobodyIsAdmin()
    {
        var path = ProductionSettingsPath();
        if (path == null) return;
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        AdminPolicy.IsAdminEmail(config, "owner@example.com").Should().BeFalse();
    }
}
