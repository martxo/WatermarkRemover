using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// Picks the LAN address to advertise in the QR code: a private IPv4 of a running interface
/// (Wi-Fi/Ethernet), preferring 192.168/10. over exotic ranges, falling back to loopback so the
/// page at least opens on the same machine in tests.
/// </summary>
public static class LanAddresses
{
    public static string BestIPv4() => BestIPv4(NetworkInterface.GetAllNetworkInterfaces());

    /// <summary>Pure selection logic; exposed for tests.</summary>
    public static string BestIPv4(IEnumerable<NetworkInterface> interfaces)
    {
        var candidates = interfaces
            .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                        i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        i.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .ToList();

        return candidates.FirstOrDefault(IsPreferredPrivate) ??
               candidates.FirstOrDefault(IsPrivate) ??
               candidates.FirstOrDefault() ??
               IPAddress.Loopback.ToString();
    }

    /// <summary>192.168.x.x / 10.x.x.x — the ranges home and office routers use.</summary>
    private static bool IsPreferredPrivate(string ip) =>
        ip.StartsWith("192.168.", StringComparison.Ordinal) ||
        ip.StartsWith("10.", StringComparison.Ordinal);

    private static bool IsPrivate(string ip) =>
        IsPreferredPrivate(ip) || Is172Range(ip);

    /// <summary>172.16.0.0 – 172.31.255.255 (the third private range, checked on the second octet).</summary>
    private static bool Is172Range(string ip)
    {
        if (!ip.StartsWith("172.", StringComparison.Ordinal)) return false;
        var parts = ip.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out int second) && second is >= 16 and <= 31;
    }
}
