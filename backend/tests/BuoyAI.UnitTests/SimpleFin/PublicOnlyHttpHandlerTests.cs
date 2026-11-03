using BuoyAI.Infrastructure.Services.SimpleFin;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.SimpleFin;

/// <summary>
/// The guard that stops a pasted token from reaching the local network. These use address
/// literals, so nothing here touches the internet or DNS.
/// </summary>
public class PublicOnlyHttpHandlerTests
{
    [Theory]
    [InlineData("https://127.0.0.1/simplefin/claim/x")]
    [InlineData("https://10.0.0.5/simplefin/claim/x")]
    [InlineData("https://192.168.1.10/simplefin/claim/x")]
    [InlineData("https://172.16.0.1/simplefin/claim/x")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/simplefin/claim/x")]
    public async Task APrivateOrLoopbackAddressIsNeverDialled(string url)
    {
        using var client = new HttpClient(PublicOnlyHttpHandler.Create()) { Timeout = TimeSpan.FromSeconds(10) };

        var act = () => client.PostAsync(url, new ByteArrayContent([]));

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        GetAllMessages(ex).Should().Contain("public address");
    }

    private static string GetAllMessages(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e != null; e = e.InnerException!)
        {
            messages.Add(e.Message);
            if (e.InnerException == null) break;
        }
        return string.Join(" | ", messages);
    }
}
