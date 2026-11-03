using BuoyAI.API.Controllers;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class BankAccountsControllerTests
{
    private readonly Mock<IBankAggregatorRegistry> _registry = new();
    private readonly Mock<ICloudPairingService> _pairing = new();
    private readonly BankAccountsController _controller;

    public BankAccountsControllerTests()
    {
        _controller = new BankAccountsController(
            Mock.Of<IBankAccountService>(), _registry.Object, _pairing.Object,
            Mock.Of<IFamilyAuthorizationService>(), NullLogger<BankAccountsController>.Instance);
    }

    private void Registered(params BankProvider[] providers) =>
        _registry.SetupGet(r => r.AvailableProviders).Returns(providers);

    private void Paired(bool paired) =>
        _pairing.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CloudPairingStatus(true, paired, "https://cloud.test", null, null, null, null));

    private async Task<string[]> ProvidersAsync() =>
        (string[])((OkObjectResult)await _controller.GetProviders(CancellationToken.None)).Value!;

    [Fact]
    public async Task Providers_ListWhatIsRegistered()
    {
        Registered(BankProvider.Plaid, BankProvider.SimpleFin);

        (await ProvidersAsync()).Should().Equal("Plaid", "SimpleFin");
    }

    [Fact]
    public async Task Providers_HideTheCloudFeed_UntilTheServerIsPaired()
    {
        Registered(BankProvider.SimpleFin, BankProvider.Bridge);
        Paired(false);

        (await ProvidersAsync()).Should().Equal("SimpleFin");
    }

    [Fact]
    public async Task Providers_OfferTheCloudFeed_OnceTheServerIsPaired()
    {
        Registered(BankProvider.SimpleFin, BankProvider.Bridge);
        Paired(true);

        (await ProvidersAsync()).Should().Equal("SimpleFin", "Bridge");
    }

    [Fact]
    public async Task Providers_DoNotAskAboutPairing_WhenThereIsNoCloudFeed()
    {
        Registered(BankProvider.Plaid);

        await ProvidersAsync();

        _pairing.Verify(p => p.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
