using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Overview;

namespace THOBOTTO.GameServers;

[SlashCommand("servers", "Game servers and their status", Contexts = [InteractionContextType.Guild])]
public sealed class ServerCommands(
    ServerWatch watch,
    OverviewBoard overview,
    AccessControl access,
    GameDig gameDig,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : GuildCommandModule
{
    private ValueTask<bool> CanManageAsync() => access.CanAsync(Guild, (GuildUser)Context.User, BotPermissions.ManageGameServers);

    [SubSlashCommand("add", "Add a game server")]
    public async Task<InteractionMessageProperties> AddAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] string game,
        [SlashCommandParameter(Description = "host or host:port (leave out the port for the game's default)")] string address,
        [SlashCommandParameter(Description = "Name to show instead of the game server's own", MaxLength = 100)] string? name = null)
    {
        if (await gameDig.FindGameAsync(game) is not { } known)
            return Replies.Ephemeral($"GameDig doesn't know a game called `{game}`. Pick one from the list.");

        if (!ServerAddress.TryParse(address, out var host, out var port))
            return Replies.Ephemeral("That isn't a valid address. Use `host` or `host:port`, e.g. `play.example.com:16261`.");

        var settings = await watch.GetSettingsAsync(GuildId);
        if (!settings.MembersCanAdd && !await CanManageAsync())
            return Replies.Ephemeral($"Only members with `{BotPermissions.ManageGameServers}` can add game servers here.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == GuildId).ToListAsync();

        if (servers.Any(s => s.Game == known.Id && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            return Replies.Ephemeral("That game server is already added.");
        if (servers.Count >= settings.MaxServers)
            return Replies.Ephemeral($"There are already {settings.MaxServers} game servers, the most this server takes.");
        if (!await CanManageAsync() && servers.Count(s => s.OwnerId == Context.User.Id) >= settings.MaxPerMember)
            return Replies.Ephemeral($"You already have {settings.MaxPerMember} game servers. Remove one first.");

        var server = new GameServer
        {
            GuildId = GuildId,
            Game = known.Id,
            Host = host,
            Port = port,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            OwnerId = Context.User.Id,
            CreatedAt = time.GetUtcNow(),
        };
        await watch.AddAsync(server);

        var reply = $"Added {known.Name} at `{server.Address}`.";
        if (!await overview.ShowsAsync(GuildId, ServerListWidget.KindId))
            reply += $"\nNo overview widget shows game servers yet; someone with `{BotPermissions.ManageOverview}` can add the Game servers block on the panel's Overview page.";
        return Replies.Ephemeral(reply);
    }

    [SubSlashCommand("remove", "Remove a game server (yours, or any with " + BotPermissions.ManageGameServers + ")")]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "Game server", AutocompleteProviderType = typeof(ServerAutocomplete))] string server)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var found = long.TryParse(server, out var id) ? await db.GameServers.FindAsync(id) : null;
        if (found is null || found.GuildId != GuildId)
            return Replies.Ephemeral("There's no such game server. Pick one from the list.");
        if (found.OwnerId != Context.User.Id && !await CanManageAsync())
            return Replies.Ephemeral($"Only <@{found.OwnerId}> or someone with `{BotPermissions.ManageGameServers}` can remove that game server.");

        await watch.RemoveAsync(found.Id, Context.User.Id);
        return Replies.Ephemeral($"Removed `{found.Address}`.");
    }

    [SubSlashCommand("list", "Show the game servers and their status")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == GuildId).OrderBy(s => s.Id).ToListAsync();
        if (servers.Count == 0)
            return Replies.Ephemeral("There are no game servers yet. Add one with `/servers add`.");

        var lines = new List<string>();
        foreach (var s in servers)
        {
            var game = (await gameDig.FindGameAsync(s.Game))?.Name ?? s.Game;
            var status = watch.LastStatus(s.Id) is { } st ? $"🟢 {st.Players}/{st.MaxPlayers}" : "⚫ offline";
            lines.Add($"{status} · {s.Name ?? game}: {game} at `{s.Address}`, added by <@{s.OwnerId}>");
        }
        return Replies.Ephemeral(string.Join('\n', lines));
    }

}

public sealed class GameAutocomplete(GameDig gameDig) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var games = await gameDig.GetGamesAsync();

        return games
            .Where(g => g.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || g.Id.Contains(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => !g.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .ThenBy(g => g.Name)
            .Take(25)
            .Select(g => new ApplicationCommandOptionChoiceProperties(Truncate($"{g.Name} ({g.Id})"), g.Id));
    }

    private static string Truncate(string text) => text.Length <= 100 ? text : text[..100];
}

public sealed class ServerAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var guildId = context.Interaction.GuildId!.Value;

        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == guildId).OrderBy(s => s.Id).ToListAsync();

        return servers
            .Select(s => (s.Id, Label: s.Name is null ? $"{s.Game} {s.Address}" : $"{s.Name} ({s.Address})"))
            .Where(s => s.Label.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => new ApplicationCommandOptionChoiceProperties(s.Label.Length <= 100 ? s.Label : s.Label[..100], s.Id.ToString()));
    }
}
