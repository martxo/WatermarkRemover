using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// Picks the LAN address to advertise in the QR code. The naive version only excluded loopback
/// and tunnel interfaces — but VMware / Hyper-V / WSL / VPN adapters report themselves as
/// Ethernet with a 192.168.x.x address, land straight in the "preferred private range" bucket,
/// and the QR then advertises an IP no phone can ever reach: same Wi-Fi, same band, blank page.
/// The selection now (a) skips virtual/VPN adapters by description, (b) prefers interfaces that
/// own a default gateway (a real path to the LAN), (c) keeps the 192.168/10-over-exotic ordering,
/// and (d) exposes the full ranked list so the UI can offer fallback addresses when the first
/// one does not work — the LANDrop-style manual escape hatch.
/// </summary>
public static class LanAddresses
{
    /// <summary>Adapters that exist for virtualization/VPN, never for the physical LAN.</summary>
    private static readonly string[] VirtualMarkers =
    {
        "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "tap", "tun",
        "loopback", "bluetooth", "vpn", "cloudbase", "qemu", "virtual", "aws vpn",
    };

    public static string BestIPv4() => BestIPv4(NetworkInterface.GetAllNetworkInterfaces());

    public static string BestIPv4(IEnumerable<NetworkInterface> interfaces) =>
        Candidates(interfaces).FirstOrDefault() ?? IPAddress.Loopback.ToString();

    /// <summary>
    /// Every usable private IPv4, best first. Pure selection logic; exposed for tests and for
    /// the transfer panel's "扫不出来？换个地址" cycling.
    /// </summary>
    public static IReadOnlyList<string> Candidates() =>
        Candidates(NetworkInterface.GetAllNetworkInterfaces());

    public static IReadOnlyList<string> Candidates(IEnumerable<NetworkInterface> interfaces)
    {
        return interfaces
            .Where(IsUsablePhysicalish)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (Address: a.Address.ToString(), Interface: i)))
            .OrderByDescending(HasGateway)          // a real path to the LAN beats everything
            .ThenByDescending(i => IsWifiOrEthernet(i.Interface.NetworkInterfaceType))
            .ThenByDescending(x => IsPreferredPrivate(x.Address))
            .ThenByDescending(x => IsPrivate(x.Address))
            .Select(x => x.Address)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        static bool HasGateway((string Address, NetworkInterface Interface) x) =>
            x.Interface.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
    }

    private static bool IsUsablePhysicalish(NetworkInterface i)
    {
        if (i.OperationalStatus != OperationalStatus.Up) return false;
        if (i.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;

        string description = i.Description.ToLowerInvariant();
        string name = i.Name.ToLowerInvariant();
        return !VirtualMarkers.Any(marker =>
            description.Contains(marker, StringComparison.Ordinal) ||
            name.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsWifiOrEthernet(NetworkInterfaceType type) =>
        type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;

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
