using System.Net;
using System.Net.Sockets;

namespace THOBOTTO.GameServers;

// Whether an address is out on the internet rather than in the bot's own network: members point the bot at game
// servers and videos.
public static class PublicAddress
{
    public static async Task<bool> IsPublicAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any))
            return false;

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return !CarriesIPv4(b);

        return !(b[0] is 0 or 10 or 127
            || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
            || (b[0] == 192 && b[1] == 168)
            || b[0] >= 224);
    }

    // An IPv6 address with an IPv4 one inside (NAT64, 6to4, Teredo, IPv4-compatible) reaches that one through a translator.
    private static bool CarriesIPv4(byte[] b)
        => b is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..]
            || b is [0x20, 0x02, ..]
            || b is [0x20, 0x01, 0x00, 0x00, ..]
            || b is [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ..];
}
