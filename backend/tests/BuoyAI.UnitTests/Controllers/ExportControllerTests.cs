using System.Security.Claims;
using BuoyAI.API.Controllers;
using BuoyAI.API.Services;
using BuoyAI.Core.DTOs.Export;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Controllers;

public class ExportControllerTests
{
    private readonly Mock<IUserDataExportService> _export = new();
    private readonly Mock<IStepUpAuthService> _stepUp = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<ILogger<ExportController>> _logger = new();

    private sealed class FakeLifetime : IHttpRequestLifetimeFeature
    {
        public int Aborts { get; private set; }
        public CancellationToken RequestAborted { get; set; }
        public void Abort() => Aborts++;
    }

    private sealed class FakeDataRate : IHttpMinResponseDataRateFeature
    {
        public MinDataRate? MinDataRate { get; set; }
    }

    private static ExportScope Scope(bool mfa, bool passkeyOnly = false) => new()
    {
        UserId = 1,
        HasMfa = mfa || passkeyOnly,
        User = new ExportUserDto(
            1, "u", "u@example.com", "Una", "L", "USD", null, null, null, DateTime.UtcNow, DateTime.UtcNow,
            true, mfa, null, null, null, null, null, null, "Quick", "Coach", false, null, null, null, false, false),
        Preferences = new ExportPreferencesDto(null, null, new ExportMfaDto(mfa, "None", false)),
        Accounts = new List<ExportAccountDto>(),
        AccountOwners = new Dictionary<int, int>(),
        BudgetIds = new List<int>(),
        GoalIds = new List<int>(),
        SubscriptionIds = new List<int>(),
        HiddenTransactionIds = new HashSet<int>(),
    };

    private ExportController Controller(
        ExportConcurrencyGate? gate = null, string? operationToken = null, CancellationToken requestAborted = default,
        FakeDataRate? dataRate = null, FakeLifetime? lifetime = null)
    {
        var controller = new ExportController(
            _export.Object, _stepUp.Object, _email.Object, gate ?? new ExportConcurrencyGate(), _logger.Object);
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test"));
        http.Response.Body = new MemoryStream();
        if (lifetime != null) http.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        if (requestAborted.CanBeCanceled) http.RequestAborted = requestAborted;
        if (dataRate != null)
        {
            http.Features.Set<IHttpMinResponseDataRateFeature>(dataRate);
        }
        if (operationToken != null)
        {
            http.Request.Headers["X-Operation-Token"] = operationToken;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private void SetupScope(bool mfa, bool passkeyOnly = false) =>
        _export.Setup(e => e.ResolveScopeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Scope(mfa, passkeyOnly));

    private static bool LoggedAt(Mock<ILogger<ExportController>> logger, LogLevel level) =>
        logger.Invocations.Any(i => i.Method.Name == "Log" && (LogLevel)i.Arguments[0] == level);

    [Fact]
    public async Task Export_Json_StreamsToBody_WithDispositionAndNoStore()
    {
        SetupScope(mfa: false);
        _export.Setup(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<ExportScope, Stream, CancellationToken>(async (_, s, ct) => await s.WriteAsync(new byte[] { 123, 125 }, ct));
        var controller = Controller();

        var result = await controller.Export("json", CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        var response = controller.HttpContext.Response;
        response.ContentType.Should().Be("application/json");
        response.Headers.ContentDisposition.ToString().Should().Contain("attachment").And.MatchRegex(@"buoy-export-\d{4}-\d{2}-\d{2}\.json");
        response.Headers.CacheControl.ToString().Should().Contain("no-store");
        ((MemoryStream)response.Body).Length.Should().Be(2);
        _export.Verify(e => e.RecordExportAsync(1, "json", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Export_Csv_UsesZipContentTypeAndName()
    {
        SetupScope(mfa: false);
        var controller = Controller();

        await controller.Export("csv", CancellationToken.None);

        controller.HttpContext.Response.ContentType.Should().Be("application/zip");
        controller.HttpContext.Response.Headers.ContentDisposition.ToString().Should().MatchRegex(@"\.zip");
        _export.Verify(e => e.WriteCsvZipAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Export_BadFormat_Returns400_AndDoesNotTouchTheService()
    {
        var result = await Controller().Export("xml", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _export.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Export_UnknownUser_Returns404()
    {
        _export.Setup(e => e.ResolveScopeAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync((ExportScope?)null);

        var result = await Controller().Export("json", CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- concurrency ----

    [Fact]
    public async Task Export_WhenTwoExportsAreAlreadyRunning_Returns429_WithoutWriting()
    {
        SetupScope(mfa: false);
        var gate = new ExportConcurrencyGate(2);
        using var a = gate.TryEnter(100);
        using var b = gate.TryEnter(101);

        var controller = Controller(gate);
        var result = await controller.Export("json", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        controller.HttpContext.Response.Headers.RetryAfter.ToString().Should().NotBeEmpty();
        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _export.Verify(e => e.RecordExportAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Export_WhenTheSameUserAlreadyHasOneRunning_Returns429_EvenWithFreeSlots()
    {
        SetupScope(mfa: false);
        var gate = new ExportConcurrencyGate(2);
        using var mine = gate.TryEnter(1);

        var result = await Controller(gate).Export("json", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Export_ReleasesItsSlot_WhenDone_AndWhenItFails()
    {
        SetupScope(mfa: false);
        var gate = new ExportConcurrencyGate(1);
        await Controller(gate).Export("json", CancellationToken.None);
        using (var next = gate.TryEnter(1)) next.Should().NotBeNull();

        _export.Setup(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        await Controller(gate).Export("json", CancellationToken.None);
        using var again = gate.TryEnter(1);
        again.Should().NotBeNull();
    }

    [Fact]
    public void Gate_GivesOutOnlyTheConfiguredNumberOfSlots_AndOnePerUser()
    {
        var gate = new ExportConcurrencyGate(2);

        var a = gate.TryEnter(1);
        gate.TryEnter(1).Should().BeNull("one export per user");
        var b = gate.TryEnter(2);
        gate.TryEnter(3).Should().BeNull("two exports on the server");
        a!.Dispose();
        a.Dispose(); // double dispose must not hand out an extra slot
        var c = gate.TryEnter(3);
        c.Should().NotBeNull();
        gate.TryEnter(4).Should().BeNull();
        gate.TryEnter(1).Should().BeNull("user 1's slot went to user 3");
        b!.Dispose();
        c!.Dispose();
    }

    // ---- failures while streaming ----

    [Fact]
    public async Task Export_FailureMidStream_AbortsTheConnection_LogsError_AndReturnsEmptyResult()
    {
        SetupScope(mfa: false);
        _export.Setup(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db went away"));
        var lifetime = new FakeLifetime();
        var controller = Controller(lifetime: lifetime);

        var result = await controller.Export("json", CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        lifetime.Aborts.Should().Be(1, "the connection must be cut, not left looking complete");
        LoggedAt(_logger, LogLevel.Error).Should().BeTrue();
        _email.Verify(e => e.SendDataExportedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Export_ClientCancellation_IsQuiet_AbortsAndDoesNotLogAnError()
    {
        SetupScope(mfa: false);
        using var cts = new CancellationTokenSource();
        _export.Setup(e => e.WriteCsvZipAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<ExportScope, Stream, CancellationToken>((_, _, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        var lifetime = new FakeLifetime();
        var controller = Controller(requestAborted: cts.Token, lifetime: lifetime);

        var result = await controller.Export("csv", cts.Token);

        result.Should().BeOfType<EmptyResult>();
        lifetime.Aborts.Should().Be(1);
        LoggedAt(_logger, LogLevel.Error).Should().BeFalse();
        LoggedAt(_logger, LogLevel.Debug).Should().BeTrue();
        _email.Verify(e => e.SendDataExportedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task Export_GivesTheServiceADeadlineLinkedToTheRequest()
    {
        SetupScope(mfa: false);
        CancellationToken seen = default;
        _export.Setup(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<ExportScope, Stream, CancellationToken>((_, _, ct) => seen = ct)
            .Returns(Task.CompletedTask);

        await Controller().Export("json", CancellationToken.None);

        seen.CanBeCanceled.Should().BeTrue("a hard deadline token is passed even if the client never cancels");
        ExportController.Deadline.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Export_SetsAMinimumResponseDataRate()
    {
        SetupScope(mfa: false);
        var rate = new FakeDataRate();

        await Controller(dataRate: rate).Export("json", CancellationToken.None);

        rate.MinDataRate.Should().NotBeNull();
        rate.MinDataRate!.BytesPerSecond.Should().BeGreaterThan(0);
    }

    // ---- notification email ----

    [Fact]
    public async Task Export_Success_SendsTheOwnerAnEmail_WithFormatOnly()
    {
        SetupScope(mfa: false);

        await Controller().Export("csv", CancellationToken.None);

        _email.Verify(e => e.SendDataExportedAsync("u@example.com", "Una", "csv", It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task Export_SlowNoticeEmail_DoesNotKeepTheUsersSlot_SoAnImmediateSecondExportIsNotRejected()
    {
        SetupScope(mfa: false);
        var emailStarted = new TaskCompletionSource();
        var releaseEmail = new TaskCompletionSource();
        _email.Setup(e => e.SendDataExportedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .Returns(async () =>
            {
                if (emailStarted.TrySetResult())
                {
                    await releaseEmail.Task; // only the first notice hangs
                }
            });
        var gate = new ExportConcurrencyGate(2);

        var first = Controller(gate).Export("json", CancellationToken.None);
        await emailStarted.Task; // the first response is complete and its email is hanging

        var second = await Controller(gate).Export("json", CancellationToken.None);

        second.Should().BeOfType<EmptyResult>("the second export must not get 429 while the first email is slow");
        releaseEmail.SetResult();
        await first;
    }

    [Fact]
    public async Task Export_EmailFailure_DoesNotFailTheExport()
    {
        SetupScope(mfa: false);
        _email.Setup(e => e.SendDataExportedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ThrowsAsync(new InvalidOperationException("no email provider"));
        var lifetime = new FakeLifetime();
        var controller = Controller(lifetime: lifetime);

        var result = await controller.Export("json", CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        lifetime.Aborts.Should().Be(0);
    }

    // ---- step-up ----

    [Fact]
    public async Task Export_UserWithMfa_WithoutFreshMfa_Returns403MfaRequired()
    {
        SetupScope(mfa: true);
        _stepUp.Setup(s => s.RequiresMfaForOperationAsync(1, ProtectedOperation.ViewSensitiveFinancialData)).ReturnsAsync(true);

        var result = await Controller().Export("json", CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(403);
        obj.Value!.ToString().Should().Contain("MfaRequired");
        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Export_PasskeyOnlyAccount_IsTreatedAsMfa_AndAskedToStepUp()
    {
        // HasMfaEnabled false but a passkey is registered: the account still has a second factor.
        SetupScope(mfa: false, passkeyOnly: true);
        _stepUp.Setup(s => s.RequiresMfaForOperationAsync(1, ProtectedOperation.ViewSensitiveFinancialData)).ReturnsAsync(true);

        var result = await Controller().Export("json", CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Export_UserWithMfa_WithRecentMfaSession_Proceeds()
    {
        SetupScope(mfa: true);
        _stepUp.Setup(s => s.RequiresMfaForOperationAsync(1, ProtectedOperation.ViewSensitiveFinancialData)).ReturnsAsync(false);

        await Controller().Export("json", CancellationToken.None);

        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Export_UserWithMfa_WithValidOperationToken_Proceeds()
    {
        SetupScope(mfa: true);
        _stepUp.Setup(s => s.ValidateOperationTokenAsync("tok", ProtectedOperation.ViewSensitiveFinancialData, 1)).ReturnsAsync(true);
        _stepUp.Setup(s => s.RequiresMfaForOperationAsync(1, ProtectedOperation.ViewSensitiveFinancialData)).ReturnsAsync(true);

        await Controller(operationToken: "tok").Export("json", CancellationToken.None);

        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Export_UserWithoutMfa_IsNotAskedToStepUp()
    {
        SetupScope(mfa: false);

        await Controller().Export("json", CancellationToken.None);

        _stepUp.Verify(s => s.RequiresMfaForOperationAsync(It.IsAny<int>(), It.IsAny<ProtectedOperation>()), Times.Never);
        _export.Verify(e => e.WriteJsonAsync(It.IsAny<ExportScope>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
