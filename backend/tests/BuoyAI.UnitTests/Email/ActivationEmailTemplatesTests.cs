using BuoyAI.Infrastructure.Email;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Email;

public class ActivationEmailTemplatesTests
{
    private const string Accounts = "https://buoy-ai.com/app/accounts";
    private const string Settings = "https://buoy-ai.com/app/settings/email-preferences";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConnectReminder_LinksToAccountsAndEmailSettings(int step)
    {
        var (html, text) = ActivationEmailTemplates.ConnectReminder(step, "Sam", 28, Accounts, Settings);

        html.Should().Contain(Accounts).And.Contain(Settings);
        text.Should().Contain(Accounts).And.Contain(Settings);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConnectReminder_HasNoEmDashes(int step)
    {
        // House style: the owner reads em dashes as an AI tell.
        var (html, text) = ActivationEmailTemplates.ConnectReminder(step, "Sam", 28, Accounts, Settings);

        html.Should().NotContain("—");
        text.Should().NotContain("—");
        ActivationEmailTemplates.ConnectReminderSubject(step, "Sam").Should().NotContain("—");
    }

    [Fact]
    public void ConnectReminder_EscapesTheNameInHtml()
    {
        var (html, _) = ActivationEmailTemplates.ConnectReminder(1, "<b>x</b>", 28, Accounts, Settings);

        html.Should().NotContain("<b>x</b>").And.Contain("&lt;b&gt;x&lt;/b&gt;");
    }

    [Fact]
    public void StepOne_StatesTheTrialDaysLeftAndThatNoCardIsNeeded()
    {
        var (_, text) = ActivationEmailTemplates.ConnectReminder(1, "Sam", 28, Accounts, Settings);

        text.Should().Contain("28 days left").And.Contain("no card needed");
    }

    [Fact]
    public void StepTwo_AsksForAReply()
    {
        var (_, text) = ActivationEmailTemplates.ConnectReminder(2, "Sam", 28, Accounts, Settings);

        text.Should().Contain("hit reply");
    }

    [Theory]
    [InlineData("Sam", "Quick question, Sam")]
    [InlineData("there", "Quick question")]
    [InlineData("", "Quick question")]
    public void StepTwoSubject_DropsTheNoNameFallback(string name, string expected)
    {
        ActivationEmailTemplates.ConnectReminderSubject(2, name).Should().Be(expected);
    }
}
