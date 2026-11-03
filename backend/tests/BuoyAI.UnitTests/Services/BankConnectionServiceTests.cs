using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class BankConnectionServiceTests
{
    private readonly Mock<IPlaidConnectionRepository> _connections = new();
    private readonly Mock<IAccountRepository> _accounts = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IBankAggregator> _simpleFin = new();
    private readonly BankConnectionService _service;

    public BankConnectionServiceTests()
    {
        _simpleFin.SetupGet(a => a.Provider).Returns(BankProvider.SimpleFin);
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, FamilyId = 4 });
        _service = new BankConnectionService(
            _connections.Object, _accounts.Object, _users.Object,
            new BankAggregatorRegistry(new[] { _simpleFin.Object }));
    }

    [Fact]
    public async Task List_ShowsEveryProvidersConnections_WithStatusAndTheReasonOnlyWhenTheUserMustAct()
    {
        _connections.Setup(c => c.GetByUserOrFamilyAsync(7, 4)).ReturnsAsync(new[]
        {
            new PlaidConnection
            {
                Id = 1, Provider = BankProvider.SimpleFin, InstitutionName = "My Bank", IsActive = true,
                Status = PlaidItemStatus.LoginRequired, RequiresUserAction = true, LastError = "Make a new setup token.",
                LastTransactionSyncAt = new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc),
            },
            new PlaidConnection
            {
                Id = 2, Provider = BankProvider.Plaid, InstitutionName = "Ally", IsActive = true,
                Status = PlaidItemStatus.Active, LastError = "an old note",
            },
        });
        _accounts.Setup(a => a.CountActiveByConnectionIdsAsync(It.IsAny<IReadOnlyCollection<int>>()))
            .ReturnsAsync(new Dictionary<int, int> { [1] = 2 });

        var list = await _service.ListAsync(7);

        list.Should().HaveCount(2);
        list[0].Provider.Should().Be("SimpleFin");
        list[0].Status.Should().Be("LoginRequired");
        list[0].RequiresUserAction.Should().BeTrue();
        list[0].Message.Should().Be("Make a new setup token.");
        list[0].AccountCount.Should().Be(2);
        list[0].LastSyncAt.Should().Be(new DateTime(2025, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        list[1].Provider.Should().Be("Plaid");
        list[1].Message.Should().BeNull();
    }

    [Fact]
    public async Task Disconnect_GoesThroughTheProviderTheConnectionCameFrom()
    {
        _connections.Setup(c => c.GetByIdAsync(1)).ReturnsAsync(new PlaidConnection { Id = 1, UserId = 7, Provider = BankProvider.SimpleFin });

        var done = await _service.DisconnectAsync(7, 1);

        done.Should().BeTrue();
        _simpleFin.Verify(a => a.DisconnectConnectionAsync(7, 1), Times.Once);
    }

    [Fact]
    public async Task Disconnect_DeactivatesTheConnectionsAccounts_SoNetWorthAndTheAccountListAgree()
    {
        _connections.Setup(c => c.GetByIdAsync(1)).ReturnsAsync(new PlaidConnection { Id = 1, UserId = 7, Provider = BankProvider.SimpleFin });
        var checking = new Account { Id = 10, PlaidConnectionId = 1, IsActive = true };
        var card = new Account { Id = 11, PlaidConnectionId = 1, IsActive = true };
        _accounts.Setup(a => a.GetByConnectionIdAsync(1)).ReturnsAsync(new[] { checking, card });

        (await _service.DisconnectAsync(7, 1)).Should().BeTrue();

        checking.IsActive.Should().BeFalse();
        card.IsActive.Should().BeFalse();
        _accounts.Verify(a => a.UpdateAsync(It.IsAny<Account>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Disconnect_ConnectionsThatAreMissingOrNotTheUsers_ReturnFalseAndTouchNothing()
    {
        _connections.Setup(c => c.GetByIdAsync(1)).ReturnsAsync(new PlaidConnection { Id = 1, UserId = 99, Provider = BankProvider.SimpleFin });

        (await _service.DisconnectAsync(7, 1)).Should().BeFalse();
        (await _service.DisconnectAsync(7, 404)).Should().BeFalse();
        _simpleFin.Verify(a => a.DisconnectConnectionAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Disconnect_AProviderThatIsNotRegistered_ReturnsFalse()
    {
        _connections.Setup(c => c.GetByIdAsync(1)).ReturnsAsync(new PlaidConnection { Id = 1, UserId = 7, Provider = BankProvider.Plaid });

        (await _service.DisconnectAsync(7, 1)).Should().BeFalse();
    }
}
