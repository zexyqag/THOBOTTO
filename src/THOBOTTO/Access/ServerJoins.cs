using System.Net;

using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;

using THOBOTTO.Integrations;

namespace THOBOTTO.Access;

// Who may add the bot to a server. A public bot can be added by anyone; limited, it leaves a server it was just added
// to unless one of its owners is in it or the server is listed, and tells that server's owner. Servers it's already in
// stay, whatever the setting. Adding it to an account (for group DMs) can't be limited, but its commands there only work
// for members of the servers it's in.
public sealed class ServerJoins(RestClient rest, IntegrationStore store, BotApplication application, TimeProvider time, ILogger<ServerJoins> logger)
{
    public const string OwnersOnly = "owners";

    // A server it joined longer ago than this is one it was already in (seen again at a start or a reconnect).
    private static readonly TimeSpan JustJoined = TimeSpan.FromMinutes(5);

    public bool Limited => store.Get(IntegrationStore.ServerJoins) == OwnersOnly;

    // Server ids, separated by commas, spaces or new lines; anything else in it is skipped.
    public static IReadOnlySet<ulong> Listed(string? text)
        => (text ?? "").Split([',', ' ', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries).Select(t => ulong.TryParse(t, out var id) ? id : 0).Where(id => id != 0).ToHashSet();

    public static bool JustAdded(DateTimeOffset joinedAt, DateTimeOffset now) => now - joinedAt < JustJoined;

    public async Task SeenAsync(Guild guild)
    {
        if (!Limited || !JustAdded(guild.JoinedAt, time.GetUtcNow()) || Listed(store.Get(IntegrationStore.ServersAllowed)).Contains(guild.Id))
            return;
        foreach (var owner in await application.OwnersAsync())
        {
            if (await InAsync(guild.Id, owner))
                return;
        }
        try
        {
            var dm = await rest.GetDMChannelAsync(guild.OwnerId);
            await dm.SendMessageAsync(new() { Content = $"I left **{guild.Name}** again: whoever runs me keeps me to their own servers. Ask them if you'd like me there." });
        }
        catch (RestException ex)
        {
            logger.LogDebug("The owner of {GuildId} couldn't be told: {Message}", guild.Id, ex.Message);
        }
        await rest.LeaveGuildAsync(guild.Id);
        logger.LogInformation("Left {Guild} ({GuildId}), which none of the bot's owners is in: who adds it to servers is limited", guild.Name, guild.Id);
    }

    private async Task<bool> InAsync(ulong guildId, ulong userId)
    {
        try
        {
            await rest.GetGuildUserAsync(guildId, userId);
            return true;
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}

public sealed class ServerJoinsHandler(ServerJoins joins, ILogger<ServerJoinsHandler> logger) : IGuildCreateGatewayHandler
{
    public ValueTask HandleAsync(GuildCreateEventArgs arg)
    {
        if (arg.Guild is { IsUnavailable: false } guild)
            _ = SeeAsync(guild);
        return default;
    }

    private async Task SeeAsync(Guild guild)
    {
        try
        {
            await joins.SeenAsync(guild);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checking who added the bot to {GuildId} failed", guild.Id);
        }
    }
}
