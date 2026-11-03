using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using BuoyAI.API.Controllers;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class ImportControllerTests
{
  private static (ImportController Controller, Mock<IImportService> Service) Create(int userId = 5)
  {
    var service = new Mock<IImportService>();
    var controller = new ImportController(service.Object, NullLogger<ImportController>.Instance)
    {
      ControllerContext = new ControllerContext
      {
        HttpContext = new DefaultHttpContext
        {
          User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "test"))
        }
      }
    };
    return (controller, service);
  }

  private static IFormFile File(string name, string content)
  {
    var bytes = Encoding.UTF8.GetBytes(content);
    return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name);
  }

  [Fact]
  public async Task Preview_PassesFilesSourceAccountAndMappingToTheService()
  {
    var (controller, service) = Create(5);
    ImportPreviewRequest? seen = null;
    IReadOnlyList<ImportFileInput>? seenFiles = null;
    service.Setup(s => s.PreviewAsync(5, It.IsAny<IReadOnlyList<ImportFileInput>>(), It.IsAny<ImportPreviewRequest>()))
      .Callback<int, IReadOnlyList<ImportFileInput>, ImportPreviewRequest>((_, f, r) => { seenFiles = f; seen = r; })
      .ReturnsAsync(new ImportPreviewResult { ImportId = "abc" });

    var result = await controller.Preview(
      new List<IFormFile> { File("bank.csv", "Date,Amount\n") },
      source: "csv",
      accountId: 9,
      mapping: "{\"dateOrder\":\"dayFirst\",\"flipSign\":true,\"amountColumn\":2}");

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ImportPreviewResult>().Which.ImportId.Should().Be("abc");
    seenFiles.Should().ContainSingle().Which.FileName.Should().Be("bank.csv");
    seen!.Source.Should().Be(ImportSource.Csv);
    seen.AccountId.Should().Be(9);
    seen.Mapping.DateOrder.Should().Be(ImportDateOrder.DayFirst);
    seen.Mapping.FlipSign.Should().BeTrue();
    seen.Mapping.AmountColumn.Should().Be(2);
  }

  [Fact]
  public async Task Preview_RejectsAnUnknownSourceOrBrokenMapping()
  {
    var (controller, _) = Create();
    var files = new List<IFormFile> { File("bank.csv", "x") };

    (await controller.Preview(files, "paypal", null, null)).Should().BeOfType<BadRequestObjectResult>();
    (await controller.Preview(files, null, null, "{not json")).Should().BeOfType<BadRequestObjectResult>();
  }

  [Fact]
  public async Task ImportExceptions_BecomeTheirStatusCodeAndMessage()
  {
    var (controller, service) = Create();
    service.Setup(s => s.CommitAsync(5, "gone", It.IsAny<ImportCommitRequest>()))
      .ThrowsAsync(new ImportException("This import has expired.", 404));

    var result = await controller.Commit("gone", new ImportCommitRequest());

    result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
  }

  [Fact]
  public async Task Commit_RunsAsTheSignedInUser()
  {
    var (controller, service) = Create(42);
    service.Setup(s => s.CommitAsync(42, "id1", It.IsAny<ImportCommitRequest>())).ReturnsAsync(new ImportCommitResult { BatchId = "b1" });

    var result = await controller.Commit("id1", new ImportCommitRequest());

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ImportCommitResult>().Which.BatchId.Should().Be("b1");
    service.Verify(s => s.CommitAsync(42, "id1", It.IsAny<ImportCommitRequest>()), Times.Once);
  }

  [Fact]
  public async Task Undo_RunsAsTheSignedInUser()
  {
    var (controller, service) = Create(42);
    service.Setup(s => s.UndoAsync(42, "b1")).ReturnsAsync(new ImportUndoResult { TransactionsRemoved = 3 });

    var result = await controller.Undo("b1");

    result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<ImportUndoResult>().Which.TransactionsRemoved.Should().Be(3);
  }
}
