namespace THOBOTTO.GameServers;

// "host" or "host:port", as typed for a game server.
public static class ServerAddress
{
    public static bool TryParse(string address, out string host, out int? port)
    {
        port = null;
        host = address.Trim();
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host.AsSpan(colon + 1), out var p) || p is < 1 or > 65535)
                return false;
            host = host[..colon];
            port = p;
        }
        return host.Length > 0 && GameDig.IsValidHost(host);
    }
}
