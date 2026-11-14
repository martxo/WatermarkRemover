using System.Net;
using System.Net.Sockets;

namespace WatermarkRemover.Netdisk.Net;

/// <summary>
/// HttpClient factory for the alipan endpoints. The 阿里云盘 CDN hands out IPv6-first URLs
/// (dl1-v6.aliyundrive.cloud), and on networks whose IPv6 route to that CDN is a black hole the
/// platform-default connect sequence hangs until HttpClient.Timeout — a few-KB file "downloads"
/// for 100 seconds and then fails. The connect callback below resolves the host itself, tries
/// IPv4 addresses first (each with a short per-address timeout) and only falls back to IPv6 when
/// no IPv4 path exists, mirroring what the aliyunpan CLI does with its preferIPType=ipv4 setting.
/// </summary>
public static class AlipanHttpClient
{
    /// <summary>One address gets this long to complete its TCP handshake before the next is tried.</summary>
    private static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(3);

    public static HttpClient Create() => Create(TimeSpan.FromSeconds(100));

    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = ConnectAsync,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Resolution failed: leave it to the platform's own path so error semantics stay standard.
            return await PlatformConnectAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            return await PlatformConnectAsync(context, cancellationToken).ConfigureAwait(false);
        }

        // IPv4 first; OrderByDescending is stable, so the platform's ordering is kept within each family.
        IPAddress[] ordered = addresses
            .OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

        Exception? lastError = null;
        foreach (IPAddress address in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(PerAddressTimeout);
            try
            {
                await socket
                    .ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                lastError = new SocketException((int)SocketError.TimedOut);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastError = ex;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async ValueTask<Stream> PlatformConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
