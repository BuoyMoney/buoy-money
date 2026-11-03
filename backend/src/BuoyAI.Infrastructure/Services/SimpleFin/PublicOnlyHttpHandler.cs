using System.Net;
using System.Net.Sockets;

namespace BuoyAI.Infrastructure.Services.SimpleFin;

/// <summary>
/// An HTTP handler that dials only public IP addresses and never follows a redirect. It checks
/// the address the socket is about to use, not just the name, so a host that resolves to a private
/// address (or changes its answer between a check and the connection) cannot reach the local network.
/// </summary>
public static class PublicOnlyHttpHandler
{
    public static HttpMessageHandler Create() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] candidates = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken);

            var publicAddresses = candidates.Where(OAuthConnectService.IsPublicIpAddress).ToArray();
            if (publicAddresses.Length == 0)
            {
                throw new HttpRequestException("The host does not resolve to a public address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(publicAddresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
