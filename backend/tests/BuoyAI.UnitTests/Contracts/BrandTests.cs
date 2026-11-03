using BuoyAI.Core.Configuration;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Contracts;

/// <summary>Brand.Name is process-wide, so these tests run alone and put the default back.</summary>
[CollectionDefinition("Brand", DisableParallelization = true)]
public class BrandCollection;

[Collection("Brand")]
public class BrandTests : IDisposable
{
    public void Dispose() => Brand.Configure(null);

    [Fact]
    public void TheDefaultNameIsBuoyMoney()
    {
        Brand.Configure(null);
        Brand.Name.Should().Be("Buoy Money");

        Brand.Configure("  ");
        Brand.Name.Should().Be("Buoy Money");
    }

    [Fact]
    public void ASettingReplacesTheDefaultNameInTextWrittenWithIt()
    {
        Brand.Configure("Acme Budget");

        Brand.Apply("Welcome to Buoy Money! The Buoy Money Team").Should().Be("Welcome to Acme Budget! The Acme Budget Team");
        // The company and the paid add-ons are not the product name.
        Brand.Apply("Buoy Money is run by BuoyAI LLC with BuoyAI Cloud").Should().Be("Acme Budget is run by BuoyAI LLC with BuoyAI Cloud");
    }

    [Fact]
    public void WithTheDefaultNameTextIsLeftAlone()
    {
        Brand.Configure(null);

        Brand.Apply("Welcome to Buoy Money!").Should().Be("Welcome to Buoy Money!");
        Brand.Apply("").Should().BeEmpty();
    }

    private static (ResendEmailService Service, Mock<IEmailTransport> Transport) EmailService()
    {
        var transport = new Mock<IEmailTransport>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var service = new ResendEmailService(
            transport.Object, config, Mock.Of<ILogger<ResendEmailService>>(), Mock.Of<IEmailPreferencesService>(),
            new BuoyAiMetrics(new BuoyAI.UnitTests.Services.TestMeterFactory()));
        return (service, transport);
    }

    [Fact]
    public async Task EmailsGoOutAsBuoyMoneyByDefault()
    {
        Brand.Configure(null);
        var (service, transport) = EmailService();
        OutboundEmail? sent = null;
        transport.Setup(t => t.SendAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>())).Callback<OutboundEmail, CancellationToken>((m, _) => sent = m).Returns(Task.CompletedTask);

        await service.SendRawEmailAsync("a@b.test", "Welcome to Buoy Money", "<p>Hi from Buoy Money</p>", "Hi from Buoy Money");

        sent.Should().NotBeNull();
        sent!.FromName.Should().Be("Buoy Money");
        sent.Subject.Should().Be("Welcome to Buoy Money");
        sent.HtmlBody.Should().Contain("Hi from Buoy Money");
    }

    [Fact]
    public async Task AServerWithItsOwnNameSendsEmailsUnderThatName()
    {
        Brand.Configure("Acme Budget");
        var (service, transport) = EmailService();
        OutboundEmail? sent = null;
        transport.Setup(t => t.SendAsync(It.IsAny<OutboundEmail>(), It.IsAny<CancellationToken>())).Callback<OutboundEmail, CancellationToken>((m, _) => sent = m).Returns(Task.CompletedTask);

        await service.SendRawEmailAsync("a@b.test", "Welcome to Buoy Money", "<p>Hi from Buoy Money</p>", "Hi from Buoy Money");

        sent!.FromName.Should().Be("Acme Budget");
        sent.Subject.Should().Be("Welcome to Acme Budget");
        sent.HtmlBody.Should().Be("<p>Hi from Acme Budget</p>");
        sent.TextBody.Should().Be("Hi from Acme Budget");
    }
}
