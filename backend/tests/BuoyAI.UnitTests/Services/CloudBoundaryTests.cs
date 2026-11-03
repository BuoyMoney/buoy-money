using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Cloud code lives under cloud/ and stays private when the app is exported to the public mirror. The app and the self-host
/// server may talk to it over HTTP, never reference it. This fails the build the day someone adds a project reference, a
/// solution entry or a source file that reaches into cloud/.
/// </summary>
public class CloudBoundaryTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "backend")) && Directory.Exists(Path.Combine(dir.FullName, "flutter"))))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static IEnumerable<string> Files(string root, string folder, params string[] patterns) =>
        patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, folder), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}.dart_tool{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}"));

    [Fact]
    public void NothingInTheAppReferencesCloudCode()
    {
        var root = RepoRoot();
        if (root == null) return; // run from a packaged test binary with no checkout: nothing to scan

        var offenders = new List<string>();
        foreach (var file in Files(root, "backend", "*.csproj", "*.sln", "*.slnx", "*.props", "*.cs")
                     .Concat(Files(root, "flutter", "*.dart", "pubspec.yaml")))
        {
            if (file.EndsWith("CloudBoundaryTests.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            if (text.Contains("BuoyAI.Cloud.", StringComparison.Ordinal)
                || text.Contains("..\\cloud\\", StringComparison.OrdinalIgnoreCase)
                || text.Contains("../cloud/", StringComparison.OrdinalIgnoreCase))
                offenders.Add(Path.GetRelativePath(root, file));
        }

        offenders.Should().BeEmpty("the app may call Cloud over HTTP but must not reference it");
    }
}
