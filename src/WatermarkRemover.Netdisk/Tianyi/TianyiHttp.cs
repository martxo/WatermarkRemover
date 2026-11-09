using System.Net;
using System.Net.Sockets;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// HTTP plumbing for cloud.189.cn. The service binds a session to the client IP: the browser
/// login may go out over IPv6 while .NET prefers IPv6 as well but the API records/expects the
/// IPv4 address (observed error: "check ip error - curlp=2409:... cookiesIp=117.183.93.41").
/// Forcing IPv4 for every 天翼 request keeps the API on the same address family as the login.
/// </summary>
public static class TianyiHttp
{
    public static SocketsHttpHandler CreateHandler(bool allowAutoRedirect = true) =>
        new()
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            ConnectCallback = ConnectIpv4FirstAsync,
        };

    /// <summary>Dials the host's IPv4 address explicitly; falls back to the OS choice when none exists.</summary>
    private static async ValueTask<Stream> ConnectIpv4FirstAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        if (IPAddress.TryParse(host, out var literal))
        {
            return await ConnectAsync(literal, port, cancellationToken).ConfigureAwait(false);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            addresses = Array.Empty<IPAddress>();
        }

        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 is not null)
        {
            return await ConnectAsync(ipv4, port, cancellationToken).ConfigureAwait(false);
        }

        var fallback = addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        return await ConnectAsync(fallback, port, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
