using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.EditionSetting;

/// <summary>
/// The Cloud boundary (owner rule, 2026-10-12): everything for BuoyAI Cloud lives under cloud/, in its
/// own projects. At the 2.0.0 launch the open-source app is exported from an allowlist and cloud/ stays
/// private, so the app and the self-host server must never reference a Cloud project, in code, in the
/// build, or in an image. They may only talk to Cloud over HTTP, through ICloudBillingClient.
/// These tests read the repository, so a slip fails the build instead of leaking into the export.
/// </summary>
public class CloudBoundaryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // cloud/ is not required: the public export has no cloud/, and these checks must still pass there.
            if (Directory.Exists(Path.Combine(dir.FullName, "backend", "src")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root (with backend/src) not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Files(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar));

    [Fact]
    public void NoAppProject_ReferencesAnythingUnderCloud()
    {
        var root = RepoRoot();
        var offenders = Files(Path.Combine(root, "backend"), "*.csproj")
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                return text.Contains("cloud" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || text.Contains("cloud/", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("BuoyAI.Cloud", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();

        offenders.Should().BeEmpty("the app must not build against Cloud code");
    }

    [Fact]
    public void NoAppSourceFile_UsesACloudNamespace()
    {
        var root = RepoRoot();
        var offenders = Files(Path.Combine(root, "backend", "src"), "*.cs")
            .Where(f => File.ReadAllText(f).Contains("BuoyAI.Cloud", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.Should().BeEmpty("the app talks to Cloud over HTTP only (ICloudBillingClient), never through its code");
    }

    [Fact]
    public void TheAppSolution_DoesNotIncludeCloud()
    {
        var root = RepoRoot();
        foreach (var sln in Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.sln"))
        {
            File.ReadAllText(sln).Should().NotContainEquivalentOf("cloud", $"{Path.GetFileName(sln)} must not build Cloud projects");
        }
    }

    [Fact]
    public void NoAppImage_CopiesCloudIn()
    {
        var root = RepoRoot();
        var dockerfiles = Directory.EnumerateFiles(root, "Dockerfile*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "backend"), "Dockerfile*", SearchOption.TopDirectoryOnly))
            .ToList();
        dockerfiles.Should().NotBeEmpty();

        foreach (var file in dockerfiles)
        {
            var copies = File.ReadLines(file)
                .Where(l => l.TrimStart().StartsWith("COPY", StringComparison.OrdinalIgnoreCase)
                         || l.TrimStart().StartsWith("ADD", StringComparison.OrdinalIgnoreCase));
            copies.Should().NotContain(l => l.Contains("cloud", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} is an app image and must not contain Cloud");
        }
    }

    [Fact]
    public void CloudDoesNotReferenceTheAppEither()
    {
        // The seam is HTTP in both directions; sharing code would make the two impossible to split.
        var root = RepoRoot();
        if (!Directory.Exists(Path.Combine(root, "cloud"))) return;   // the public export has no cloud/

        var offenders = Files(Path.Combine(root, "cloud"), "*.csproj")
            .Where(p => File.ReadAllText(p).Contains("backend", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p))
            .ToList();
        offenders.Should().BeEmpty();

        var sources = Files(Path.Combine(root, "cloud"), "*.cs")
            .Where(f => File.ReadAllText(f).Contains("using BuoyAI.Core", StringComparison.Ordinal)
                     || File.ReadAllText(f).Contains("using BuoyAI.Infrastructure", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        sources.Should().BeEmpty();
    }

    [Fact]
    public void TheOnlyCloudSeam_InTheApp_IsTheHttpClientInterface()
    {
        // The interface is on the app side, in Core, and has no Stripe or Cloud types in it.
        var root = RepoRoot();
        var iface = File.ReadAllText(Path.Combine(root, "backend", "src", "BuoyAI.Core", "Interfaces", "ICloudBillingClient.cs"));
        iface.Should().Contain("interface ICloudBillingClient");
        iface.Should().NotContain("Stripe.");
    }
}
