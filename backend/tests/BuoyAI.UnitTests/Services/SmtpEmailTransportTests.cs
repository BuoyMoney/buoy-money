using System.Net;
using System.Net.Sockets;
using System.Text;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The SMTP transport talks to a real socket here: a minimal in-process SMTP server that records
/// the envelope and the message. The full path goes through ResendEmailService, the single send point.
/// </summary>
public class SmtpEmailTransportTests
{
    /// <summary>Plain-text SMTP with no TLS and no auth, enough for MailKit's SecureSocketOptions.None.</summary>
    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _loop;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public List<string> Commands { get; } = new();
        public string Data { get; private set; } = "";

        public FakeSmtpServer()
        {
            _listener.Start();
            _loop = Task.Run(Serve);
        }

        private async Task Serve()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                async Task Say(string line) => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                await Say("220 fake.test ESMTP");
                while (await reader.ReadLineAsync() is { } line)
                {
                    Commands.Add(line);
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO": await Say("250 fake.test"); break;
                        case "MAIL":
                        case "RCPT": await Say("250 OK"); break;
                        case "DATA":
                            await Say("354 go ahead");
                            var sb = new StringBuilder();
                            while (await reader.ReadLineAsync() is { } d && d != ".") sb.AppendLine(d);
                            Data = sb.ToString();
                            await Say("250 queued");
                            break;
                        case "QUIT": await Say("221 bye"); return;
                        default: await Say("250 OK"); break;
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _loop; } catch { }
        }
    }

    private static IConfiguration Config(int port, Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "127.0.0.1",
            ["Smtp:Port"] = port.ToString(),
            ["Smtp:Security"] = "none",
            ["Smtp:FromAddress"] = "money@home.test",
            ["Smtp:FromName"] = "Home Money",
        };
        if (extra != null) foreach (var kv in extra) values[kv.Key] = kv.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public async Task Sends_ToTheConfiguredServer_FromTheConfiguredAddress()
    {
        await using var server = new FakeSmtpServer();
        var transport = new SmtpEmailTransport(Config(server.Port));

        await transport.SendAsync(new OutboundEmail(
            "BuoyAI", "noreply@buoy-ai.com", "reader@home.test", "support@buoy-ai.com",
            "Your code", "<p>123456</p>", "123456"));

        server.Commands.Should().Contain(c => c.StartsWith("MAIL FROM:<money@home.test>", StringComparison.OrdinalIgnoreCase),
            "the hosted noreply@buoy-ai.com address must never be used as the sender on a home server");
        server.Commands.Should().Contain(c => c.StartsWith("RCPT TO:<reader@home.test>", StringComparison.OrdinalIgnoreCase));
        server.Data.Should().Contain("Subject: Your code").And.Contain("Home Money").And.Contain("123456");
        server.Data.Should().NotContain("support@buoy-ai.com", "hosted's reply-to is not used");
    }

    [Fact]
    public async Task ReplyTo_IsTheOperatorsAddress_WhenConfigured()
    {
        await using var server = new FakeSmtpServer();
        var transport = new SmtpEmailTransport(Config(server.Port, new() { ["Smtp:ReplyTo"] = "me@home.test" }));

        await transport.SendAsync(new OutboundEmail("B", "a@b.test", "r@home.test", "support@buoy-ai.com", "S", "<p>x</p>", "x"));

        server.Data.Should().Contain("Reply-To: me@home.test");
    }

    [Fact]
    public async Task TheSingleSendPoint_ReachesSmtp_ForARealEmail()
    {
        await using var server = new FakeSmtpServer();
        var transport = new SmtpEmailTransport(Config(server.Port));
        var service = new ResendEmailService(
            transport,
            Config(server.Port, new() { ["App:BaseUrl"] = "http://home.test:8080" }),
            NullLogger<ResendEmailService>.Instance,
            new Mock<IEmailPreferencesService>().Object,
            new BuoyAI.Infrastructure.Telemetry.BuoyAiMetrics(new TestMeterFactory()));

        await service.SendRawEmailAsync("reader@home.test", "Hello from home", "<p>hi</p>", "hi");

        server.Data.Should().Contain("Subject: Hello from home");
    }

    [Fact]
    public async Task BadSecuritySetting_IsReportedPlainly()
    {
        var transport = new SmtpEmailTransport(Config(25, new() { ["Smtp:Security"] = "maybe" }));

        var act = () => transport.SendAsync(new OutboundEmail("B", "a@b.test", "r@home.test", null, "S", "<p>x</p>", "x"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*starttls, tls or none*");
    }

    [Fact]
    public void IsSmtpConfigured_NeedsAHost()
    {
        SmtpEmailTransport.IsSmtpConfigured(new ConfigurationBuilder().Build()).Should().BeFalse();
        SmtpEmailTransport.IsSmtpConfigured(Config(25)).Should().BeTrue();
    }

    [Fact]
    public void IsSmtpConfigured_NeedsASender_NotJustAHost()
    {
        var hostOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Smtp:Host"] = "mail.home.test" }).Build();
        var userIsAddress = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Smtp:Host"] = "mail.home.test", ["Smtp:Username"] = "me@home.test" }).Build();

        SmtpEmailTransport.IsSmtpConfigured(hostOnly).Should().BeFalse();
        SmtpEmailTransport.IsSmtpConfigured(userIsAddress).Should().BeTrue();
    }

    [Fact]
    public async Task WithNoFromAddress_ItRefusesToSendAsTheHostedDomain()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "127.0.0.1", ["Smtp:Security"] = "none", ["Smtp:Username"] = "not-an-address",
        }).Build();

        var act = () => new SmtpEmailTransport(config).SendAsync(
            new OutboundEmail("B", "noreply@buoy-ai.com", "r@home.test", null, "S", "<p>x</p>", "x"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Smtp:FromAddress*");
    }
}
