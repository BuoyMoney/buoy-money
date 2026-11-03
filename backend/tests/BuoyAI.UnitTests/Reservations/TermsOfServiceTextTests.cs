using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Reservations;

public class TermsOfServiceTextTests
{
    private static string Terms()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "legal", "TERMS_OF_SERVICE.md")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the repo's legal folder must be reachable from the test output");
        return File.ReadAllText(Path.Combine(dir!.FullName, "legal", "TERMS_OF_SERVICE.md"));
    }

    [Fact]
    public void FoundingSection_StatesTheFirst150SpotCap_AndCountdownIsForSelfHostLaunch()
    {
        var text = Terms();
        var start = text.IndexOf("### 9.9", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var end = text.IndexOf("### 9.10", start, StringComparison.Ordinal);
        var section = text[start..end];

        section.Should().Contain("first 150 paid $1 reservations");
        section.Should().Contain("launch of self-hosting");
        section.Should().Contain("does not mark when founding spots end");
    }

    [Fact]
    public void SubSectionNumbers_MatchTheirParentHeading()
    {
        var text = Terms();
        var parent = "";
        foreach (var line in text.Split('\n'))
        {
            var h2 = Regex.Match(line, @"^## (\d+)\.");
            if (h2.Success) { parent = h2.Groups[1].Value; continue; }
            var h3 = Regex.Match(line, @"^### (\d+)\.\d+");
            if (h3.Success)
                h3.Groups[1].Value.Should().Be(parent, $"sub-heading '{line.Trim()}' sits under section {parent}");
        }
    }
}
