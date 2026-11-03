using System.Security.Claims;
using System.Threading.Tasks;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class BudgetTransferControllerTests
{
  private readonly Mock<IBudgetService> _budgetService = new();
  private readonly Mock<IBudgetTransferService> _transferService = new();
  private readonly Mock<IFamilyAuthorizationService> _authService = new();
  private readonly Mock<IUserRepository> _userRepository = new();
  private readonly BudgetTransferController _controller;

  public BudgetTransferControllerTests()
  {
    _controller = new BudgetTransferController(
      _budgetService.Object, _transferService.Object, _authService.Object, _userRepository.Object,
      new Mock<ILogger<BudgetTransferController>>().Object);

    var httpContext = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
          new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")),
    };
    _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
  }

  [Fact]
  public async Task TransferFunds_ReadOnlyMemberDenied_ForbidsAndMovesNoMoney()
  {
    _budgetService.Setup(s => s.GetBudgetCategoryAsync(20))
      .ReturnsAsync(new BudgetCategoryDto { Id = 20, BudgetId = 5 });
    _authService.Setup(a => a.CanAccessBudgetAsync(7, 5, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Denied("Read-only members cannot edit budgets"));

    var result = await _controller.TransferFunds(20, new TransferFundsRequest { SourceCategoryId = 21, Amount = 50 });

    result.Should().BeOfType<ForbidResult>();
    _transferService.Verify(s => s.TransferFundsAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TransferFundsRequest>()), Times.Never);
  }

  [Fact]
  public async Task TransferFunds_AuthorizedMember_PerformsTransfer()
  {
    _budgetService.Setup(s => s.GetBudgetCategoryAsync(20))
      .ReturnsAsync(new BudgetCategoryDto { Id = 20, BudgetId = 5 });
    _authService.Setup(a => a.CanAccessBudgetAsync(7, 5, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Allowed());
    _transferService.Setup(s => s.TransferFundsAsync(20, 7, It.IsAny<TransferFundsRequest>()))
      .ReturnsAsync(new TransferFundsResponse());

    var result = await _controller.TransferFunds(20, new TransferFundsRequest { SourceCategoryId = 21, Amount = 50 });

    result.Should().BeOfType<OkObjectResult>();
    _transferService.Verify(s => s.TransferFundsAsync(20, 7, It.IsAny<TransferFundsRequest>()), Times.Once);
  }

  [Fact]
  public async Task ResetAllRollovers_ReadOnlyMemberDenied_ForbidsAndResetsNothing()
  {
    _authService.Setup(a => a.CanAccessBudgetAsync(7, 5, ResourcePermission.Edit))
      .ReturnsAsync(AuthorizationResult.Denied("Read-only members cannot edit budgets"));

    var result = await _controller.ResetAllRollovers(5);

    result.Should().BeOfType<ForbidResult>();
    _transferService.Verify(s => s.ResetAllRolloversAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
  }
}
