using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Sync;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class SyncControllerTests
{
  private readonly Mock<ISyncService> _syncService = new();
  private readonly SyncController _controller;

  public SyncControllerTests()
  {
    _controller = new SyncController(
      _syncService.Object, new Mock<ILogger<SyncController>>().Object);

    var httpContext = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, "7") }, "test")),
    };
    _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
  }

  private static SyncOperation ValidOp(string? key = "k1", string? type = "notification_mark_read") => new()
  {
    IdempotencyKey = key ?? string.Empty,
    OperationType = type ?? string.Empty,
    EntityId = "1",
  };

  [Fact]
  public async Task ProcessBatch_NullOperations_ReturnsBadRequest()
  {
    var result = await _controller.ProcessBatch(new SyncBatchRequest { Operations = null! });

    result.Result.Should().BeOfType<BadRequestObjectResult>();
    _syncService.Verify(s => s.ProcessBatchAsync(It.IsAny<int>(), It.IsAny<SyncBatchRequest>()), Times.Never);
  }

  [Fact]
  public async Task ProcessBatch_EmptyOperations_ReturnsBadRequest()
  {
    var result = await _controller.ProcessBatch(new SyncBatchRequest { Operations = new List<SyncOperation>() });

    result.Result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task ProcessBatch_MoreThanFiftyOperations_ReturnsBadRequest()
  {
    var request = new SyncBatchRequest
    {
      Operations = Enumerable.Range(0, 51).Select(i => ValidOp(key: $"k{i}")).ToList(),
    };

    var result = await _controller.ProcessBatch(request);

    result.Result.Should().BeOfType<BadRequestObjectResult>();
    _syncService.Verify(s => s.ProcessBatchAsync(It.IsAny<int>(), It.IsAny<SyncBatchRequest>()), Times.Never);
  }

  [Fact]
  public async Task ProcessBatch_OperationMissingIdempotencyKey_ReturnsBadRequest()
  {
    var request = new SyncBatchRequest { Operations = new List<SyncOperation> { ValidOp(key: "") } };

    var result = await _controller.ProcessBatch(request);

    result.Result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task ProcessBatch_OperationMissingOperationType_ReturnsBadRequest()
  {
    var request = new SyncBatchRequest { Operations = new List<SyncOperation> { ValidOp(type: "") } };

    var result = await _controller.ProcessBatch(request);

    result.Result.Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task ProcessBatch_ValidBatch_ReturnsOkWithServiceResponse()
  {
    var serviceResponse = new SyncBatchResponse
    {
      Results = new List<SyncOperationResult>
      {
        new() { IdempotencyKey = "k1", Status = SyncOperationStatus.Applied },
      },
    };
    _syncService.Setup(s => s.ProcessBatchAsync(7, It.IsAny<SyncBatchRequest>()))
      .ReturnsAsync(serviceResponse);

    var request = new SyncBatchRequest { Operations = new List<SyncOperation> { ValidOp() } };

    var result = await _controller.ProcessBatch(request);

    var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
    ok.Value.Should().BeSameAs(serviceResponse);
    _syncService.Verify(s => s.ProcessBatchAsync(7, request), Times.Once);
  }
}
