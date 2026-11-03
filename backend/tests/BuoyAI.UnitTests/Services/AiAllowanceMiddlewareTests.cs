using System.Text.Json;
using BuoyAI.API.Middleware;
using BuoyAI.Core.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class AiAllowanceMiddlewareTests
{
    private static async Task<(int Status, JsonElement Body)> Run(Exception thrown)
    {
        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Production");
        var middleware = new ErrorHandlingMiddleware(_ => throw thrown, NullLogger<ErrorHandlingMiddleware>.Instance, env.Object);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task AllowanceUsedUp_Is402_WithTheCode_AndTheOfferToTurnOnPayAsYouGo()
    {
        var (status, body) = await Run(new AiAllowanceException("allowance_exhausted", "You've used this period's AI allowance.", canEnablePayg: true));

        status.Should().Be(402);
        body.GetProperty("message").GetString().Should().Be("You've used this period's AI allowance.");
        body.GetProperty("code").GetString().Should().Be("allowance_exhausted");
        body.GetProperty("canEnablePayg").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task APlainOutage_IsStill503()
    {
        var (status, body) = await Run(new AiUnavailableException("AI is paused.", null));

        status.Should().Be(503);
        body.GetProperty("message").GetString().Should().Be("AI is paused.");
    }
}
