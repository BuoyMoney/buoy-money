using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Contracts;

/// <summary>
/// Keeps the old product name out of anything a person can read: emails, push text, the MCP server, the AI's own wording,
/// the legal pages and the marketing site. The product is Buoy Money; the company stays BuoyAI LLC; the paid hosted
/// add-ons are BuoyAI Cloud. Identifiers keep their old spelling on purpose (namespaces, the database name, the JWT issuer
/// and audience, hosts, OAuth scopes, the passkey relying-party id) because changing them would sign people out or orphan
/// stored data. The Flutter app has its own guard in <c>flutter/test/lint/brand_guard_test.dart</c>.
/// </summary>
public class RenameGuardTests
{
    // What may still say "BuoyAI" on a line: code identifiers, the company, the Cloud product, and "formerly BuoyAI".
    private static readonly Regex Allowed = new(
        @"BuoyAI\.[A-Za-z]|BuoyAI[A-Za-z_]|BuoyAI-Users|BuoyAI LLC|BuoyAI Cloud|formerly BuoyAI|GetConnectionString\(""BuoyAI""\)",
        RegexOptions.Compiled);

    private static readonly Regex OldName = new(@"BuoyAI|Buoy AI", RegexOptions.Compiled);

    // Files where the old spelling is an identifier or an external name, not copy. Each line says why.
    private static readonly string[] BackendInternal =
    [
        "BuoyAI.API/Program.cs",                                  // log folder, logger names, Swagger title, connection string
        "BuoyAI.AppHost/Program.cs",                              // database name
        "BuoyAI.ServiceDefaults/Extensions.cs",                   // OpenTelemetry meter and source names
        "BuoyAI.Infrastructure/Telemetry/BuoyAiMetrics.cs",       // meter name
        "BuoyAI.Infrastructure/Services/EncryptionService.cs",    // key derivation salt: changing it makes stored secrets unreadable
        "BuoyAI.Infrastructure/DependencyInjection.cs",           // connection string name
        "BuoyAI.Infrastructure/Data/DataSeeder.cs",               // local seed data
        "BuoyAI.Infrastructure/Data/ReviewerAccountSeeder.cs",    // fixture bank names for store reviewers
        "BuoyAI.Infrastructure/Data/DemoAccountSeeder.cs",        // demo fixture
        "BuoyAI.Infrastructure/MCP/McpConnectToolService.cs",     // log lines
        "BuoyAI.Infrastructure/Services/RevenueCatSyncService.cs",// log line
        "BuoyAI.Infrastructure/Services/Bridge/BridgeMapping.cs", // id prefix
    ];

    // Marketing and legal files that name the old product on purpose.
    private static readonly string[] SiteAllowed =
    [
        "marketing/src/components/FeaturedIn.astro",     // alt text of the Product Hunt listing, which is still named BuoyAI
        "marketing/src/pages/oauth/[platform].astro",    // the app name registered with TikTok and Meta for the video sign-in page
        "marketing/src/pages/founding.astro",            // "BuoyAI is becoming Buoy Money", the announcement itself
        "marketing/src/data/story.ts",                   // generated title that our-story.astro strips the suffix from
        "marketing/src/pages/our-story.astro",           // the regex that strips that suffix
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // backend/ and legal/ side by side: true in this repo and in the public export (which has no CLAUDE.md).
        while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "backend")) && Directory.Exists(Path.Combine(dir.FullName, "legal"))))
            dir = dir.Parent;
        dir.Should().NotBeNull("the guard reads files from the repository");
        return dir!.FullName;
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("/*") || t.StartsWith("<!--") || t.StartsWith("#");
    }

    private static IEnumerable<string> Offences(string root, string file, bool skipUsingAndNamespace)
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        var n = 0;
        foreach (var line in File.ReadLines(file))
        {
            n++;
            if (IsComment(line)) continue;
            if (skipUsingAndNamespace && Regex.IsMatch(line, @"^\s*(namespace|using)\s")) continue;
            // A trailing comment on a code line is not copy.
            var code = Regex.Replace(line, @"\s//\s.*$", "");
            if (OldName.IsMatch(Allowed.Replace(code, "")))
                yield return $"{rel}:{n}: {line.Trim()}";
        }
    }

    [Fact]
    public void NoBackendTextSaysBuoyAIOutsideTheAllowedForms()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "backend", "src");
        var found = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => !BackendInternal.Any(i => f.Replace('\\', '/').EndsWith("/" + i, StringComparison.Ordinal)))
            .SelectMany(f => Offences(root, f, skipUsingAndNamespace: true))
            .ToList();

        found.Should().BeEmpty("the product is called Buoy Money (use that, or Brand.Name):\n" + string.Join("\n", found));
    }

    [Fact]
    public void TheShippedSettingsNameTheProductBuoyMoney()
    {
        var root = RepoRoot();
        var found = new List<string>();
        // Tracked files only: the gitignored appsettings.json / appsettings.Development.json are local and may predate the rename.
        foreach (var file in new[] { "appsettings.Example.json", "appsettings.Production.json", "appsettings.Staging.json" }
                     .Select(f => Path.Combine(root, "backend", "src", "BuoyAI.API", f))
                     .Where(File.Exists))
        {
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                // Display names only. Issuer, audience, the relying-party id and connection names are identifiers and stay.
                if (Regex.IsMatch(line, @"""(ClientName|RelyingPartyName|FromName)""\s*:\s*""[^""]*BuoyAI"))
                    found.Add($"{Path.GetFileName(file)}:{n}: {line.Trim()}");
            }
        }

        found.Should().BeEmpty(string.Join("\n", found));
    }

    [Fact]
    public void TheLegalPagesAndTheMarketingSiteNameTheProductBuoyMoney()
    {
        var root = RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "legal"), "*.md")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "marketing", "src"), "*", SearchOption.AllDirectories)
                .Where(f => new[] { ".astro", ".md", ".mdx", ".ts" }.Contains(Path.GetExtension(f))))
            .Where(f => !SiteAllowed.Any(a => f.Replace('\\', '/').EndsWith("/" + a, StringComparison.Ordinal)))
            .ToList();
        files.Should().NotBeEmpty();

        var found = files.SelectMany(f => Offences(root, f, skipUsingAndNamespace: false)).ToList();

        found.Should().BeEmpty("legal and marketing copy says Buoy Money, and BuoyAI LLC for the company:\n" + string.Join("\n", found));
    }

    [Fact]
    public void TheIdentifiersThatMustNotChangeAreStillTheOldOnes()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "backend", "src", "BuoyAI.API", "appsettings.Production.json");
        // The public export leaves the production settings out; the check runs in the private repo.
        if (!File.Exists(path)) return;
        var production = File.ReadAllText(path);

        // Changing the relying-party id would make every saved passkey stop working.
        production.Should().Contain(@"""RelyingPartyId"": ""buoy-ai.com""");
        // Changing the issuer or audience would sign everyone out.
        production.Should().MatchRegex(@"""Issuer"":\s*""BuoyAI""");
        production.Should().MatchRegex(@"""Audience"":\s*""BuoyAI-Users""");
    }
}
