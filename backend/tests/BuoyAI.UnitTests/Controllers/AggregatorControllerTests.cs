using System.Security.Claims;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class AggregatorControllerTests
{
    private readonly Mock<IBankAggregator> _simpleFin = new();
    private readonly Mock<IBankConnectionService> _connections = new();
    private readonly Mock<IFamilyAuthorizationService> _auth = new();
    private readonly AggregatorController _controller;

    public AggregatorControllerTests()
    {
        _simpleFin.SetupGet(a => a.Provider).Returns(BankProvider.SimpleFin);
        _simpleFin.Setup(a => a.CompleteConnectionAsync(7, "token"))
            .ReturnsAsync(new AggregatorConnectResult { Provider = BankProvider.SimpleFin, Success = true });
        _auth.Setup(a => a.CanManageBankConnectionsAsync(7)).ReturnsAsync(AuthorizationResult.Allowed());

        _controller = new AggregatorController(
            new BankAggregatorRegistry(new[] { _simpleFin.Object }), _connections.Object, _auth.Object,
            NullLogger<AggregatorController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")),
                },
            },
        };
    }

    [Fact]
    public void Providers_ListsOnlyWhatIsRegistered()
    {
        var result = _controller.GetAvailableProviders().Should().BeOfType<OkObjectResult>().Subject;

        result.Value.Should().BeEquivalentTo(new[] { "SimpleFin" });
    }

    [Fact]
    public async Task Complete_PassesThePastedTokenToTheProvider()
    {
        var result = await _controller.CompleteConnection(BankProvider.SimpleFin, new AggregatorController.CompleteConnectionBody { Artifact = "token" });

        result.Should().BeOfType<OkObjectResult>();
        _simpleFin.Verify(a => a.CompleteConnectionAsync(7, "token"), Times.Once);
    }

    [Fact]
    public async Task Complete_ForAProviderThatIsNotAvailable_Is404()
    {
        var result = await _controller.CompleteConnection(BankProvider.Plaid, new AggregatorController.CompleteConnectionBody { Artifact = "token" });

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ConnectingAndDisconnecting_AreRefusedForAMemberWhoMayNotManageBanks()
    {
        _auth.Setup(a => a.CanManageBankConnectionsAsync(7)).ReturnsAsync(AuthorizationResult.Denied("Only owners."));

        var start = await _controller.StartConnection(BankProvider.SimpleFin);
        var complete = await _controller.CompleteConnection(BankProvider.SimpleFin, new AggregatorController.CompleteConnectionBody { Artifact = "token" });
        var disconnect = await _controller.Disconnect(1);

        new[] { start, complete, disconnect }.Should().AllSatisfy(r =>
            r.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403));
        _simpleFin.Verify(a => a.StartConnectionAsync(It.IsAny<int>(), It.IsAny<string?>()), Times.Never);
        _simpleFin.Verify(a => a.CompleteConnectionAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
        _connections.Verify(c => c.DisconnectAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Connections_AreListedForTheSignedInUser()
    {
        _connections.Setup(c => c.ListAsync(7)).ReturnsAsync(new[] { new BankConnectionSummaryDto { Id = 1, Provider = "SimpleFin" } });

        var result = await _controller.GetConnections();

        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<IReadOnlyList<BankConnectionSummaryDto>>()
            .Which.Should().ContainSingle();
    }

    [Fact]
    public async Task Disconnect_Is200WhenDone_And404WhenNotTheUsers()
    {
        _connections.Setup(c => c.DisconnectAsync(7, 1)).ReturnsAsync(true);
        _connections.Setup(c => c.DisconnectAsync(7, 2)).ReturnsAsync(false);

        (await _controller.Disconnect(1)).Should().BeOfType<OkObjectResult>();
        (await _controller.Disconnect(2)).Should().BeOfType<NotFoundObjectResult>();
    }
}
