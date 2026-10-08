using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.GameServers;

[SlashCommand("servers", "Game server status board", Contexts = [InteractionContextType.Guild])]
public sealed class ServerCommands(
    ServerBoardService board,
    AccessControl access,
    GameDig gameDig,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    TimeProvider time) : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Interaction.GuildId!.Value;

    private ValueTask<bool> CanManageAsync() => access.CanAsync(Context.Guild!, (GuildUser)Context.User, BotPermissions.ManageServers);

    [SubSlashCommand("board", "Choose the channel that shows server status")]
    [RequirePermission(BotPermissions.ManageServers)]
    public async Task<InteractionMessageProperties> BoardAsync(
        [SlashCommandParameter(Description = "Text channel", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel channel)
    {
        if ((await board.GetSettingsAsync(GuildId)).BoardChannelId == channel.Id)
            return Replies.Ephemeral($"Server status already goes to <#{channel.Id}>.");

        await board.UpdateSettingsAsync(GuildId, Context.User.Id, $"board={channel.Id}", s => s.BoardChannelId = channel.Id);

        var reply = $"Server status now goes to <#{channel.Id}>.";
        if (!await modules.IsEnabledAsync(GuildId, ServerBoardService.ModuleId))
            reply += $"\nThe `{ServerBoardService.ModuleId}` module is off; turn it on with `/modules enable`.";
        return Replies.Ephemeral(reply);
    }

    [SubSlashCommand("settings", "Show or change the server board settings")]
    [RequirePermission(BotPermissions.ManageServers)]
    public async Task<InteractionMessageProperties> SettingsAsync(
        [SlashCommandParameter(Name = "members-can-add", Description = "Whether everyone can add servers, not just servers.manage")] bool? membersCanAdd = null,
        [SlashCommandParameter(Name = "per-member", Description = "Servers each member can add", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? perMember = null,
        [SlashCommandParameter(Name = "max-servers", Description = "Servers on the board in total", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? maxServers = null,
        [SlashCommandParameter(Name = "poll-seconds", Description = "How often servers are checked", MinValue = ServerSettings.MinPollSeconds, MaxValue = ServerSettings.MaxPollSeconds)] int? pollSeconds = null,
        [SlashCommandParameter(Name = "offline-after", Description = "Failed checks in a row before a server shows as offline", MinValue = 1, MaxValue = ServerSettings.MaxFailuresBeforeOffline)] int? offlineAfter = null)
    {
        var before = await board.GetSettingsAsync(GuildId);
        var changes = new List<string>();
        if (membersCanAdd is { } m && m != before.MembersCanAdd) changes.Add($"members-can-add={m}");
        if (perMember is { } p && p != before.MaxPerMember) changes.Add($"per-member={p}");
        if (maxServers is { } x && x != before.MaxServers) changes.Add($"max-servers={x}");
        if (pollSeconds is { } s && s != before.PollSeconds) changes.Add($"poll-seconds={s}");
        if (offlineAfter is { } o && o != before.FailuresBeforeOffline) changes.Add($"offline-after={o}");
        var asked = membersCanAdd is not null || perMember is not null || maxServers is not null || pollSeconds is not null || offlineAfter is not null;

        if (changes.Count > 0)
        {
            await board.UpdateSettingsAsync(GuildId, Context.User.Id, string.Join(' ', changes), settings =>
            {
                settings.MembersCanAdd = membersCanAdd ?? settings.MembersCanAdd;
                settings.MaxPerMember = perMember ?? settings.MaxPerMember;
                settings.MaxServers = maxServers ?? settings.MaxServers;
                settings.PollSeconds = pollSeconds ?? settings.PollSeconds;
                settings.FailuresBeforeOffline = offlineAfter ?? settings.FailuresBeforeOffline;
            });
        }

        var current = await board.GetSettingsAsync(GuildId);
        return Replies.Ephemeral($"""
            {(changes.Count > 0 ? "Updated. " : asked ? "Nothing changed. " : "")}Current settings:
            Board: {(current.BoardChannelId is { } c ? $"<#{c}>" : "not set (`/servers board`)")}
            Members can add servers: {(current.MembersCanAdd ? "yes" : "no, only `servers.manage`")}
            Per member: {current.MaxPerMember}
            Max servers: {current.MaxServers}
            Checked every {current.PollSeconds} s
            Offline after {current.FailuresBeforeOffline} failed check(s) in a row
            """);
    }

    [SubSlashCommand("add", "Add a game server to the board")]
    public async Task<InteractionMessageProperties> AddAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] string game,
        [SlashCommandParameter(Description = "host or host:port (leave out the port for the game's default)")] string address,
        [SlashCommandParameter(Description = "Name to show instead of the server's own", MaxLength = 100)] string? name = null)
    {
        if (await gameDig.FindGameAsync(game) is not { } known)
            return Replies.Ephemeral($"GameDig doesn't know a game called `{game}`. Pick one from the list.");

        if (!TryParseAddress(address.Trim(), out var host, out var port))
            return Replies.Ephemeral("That isn't a valid address. Use `host` or `host:port`, e.g. `play.example.com:16261`.");

        var settings = await board.GetSettingsAsync(GuildId);
        if (!settings.MembersCanAdd && !await CanManageAsync())
            return Replies.Ephemeral("Only members with `servers.manage` can add servers here.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == GuildId).ToListAsync();

        if (servers.Any(s => s.Game == known.Id && s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            return Replies.Ephemeral("That server is already on the board.");
        if (servers.Count >= settings.MaxServers)
            return Replies.Ephemeral($"The board is full ({settings.MaxServers} servers).");
        if (!await CanManageAsync() && servers.Count(s => s.OwnerId == Context.User.Id) >= settings.MaxPerMember)
            return Replies.Ephemeral($"You already have {settings.MaxPerMember} servers on the board. Remove one first.");

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
        await board.AddAsync(server);

        var reply = $"Added {known.Name} at `{server.Address}`. It shows up on the board shortly.";
        if (settings.BoardChannelId is null)
            reply += "\nThere's no board channel yet; someone with `servers.manage` can set one with `/servers board`.";
        return Replies.Ephemeral(reply);
    }

    [SubSlashCommand("remove", "Remove a game server (yours, or any with servers.manage)")]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "Server", AutocompleteProviderType = typeof(ServerAutocomplete))] string server)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var found = long.TryParse(server, out var id) ? await db.GameServers.FindAsync(id) : null;
        if (found is null || found.GuildId != GuildId)
            return Replies.Ephemeral("There's no such server. Pick one from the list.");
        if (found.OwnerId != Context.User.Id && !await CanManageAsync())
            return Replies.Ephemeral($"Only <@{found.OwnerId}> or someone with `servers.manage` can remove that server.");

        await board.RemoveAsync(found.Id, Context.User.Id);
        return Replies.Ephemeral($"Removed `{found.Address}`.");
    }

    [SubSlashCommand("list", "Show the servers on the board")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == GuildId).OrderBy(s => s.Id).ToListAsync();
        if (servers.Count == 0)
            return Replies.Ephemeral("There are no servers yet. Add one with `/servers add`.");

        var lines = new List<string>();
        foreach (var s in servers)
        {
            var game = (await gameDig.FindGameAsync(s.Game))?.Name ?? s.Game;
            lines.Add($"{s.Name ?? game}: {game} at `{s.Address}`, added by <@{s.OwnerId}>");
        }
        return Replies.Ephemeral(string.Join('\n', lines));
    }

    private static bool TryParseAddress(string address, out string host, out int? port)
    {
        port = null;
        host = address;

        var colon = address.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(address.AsSpan(colon + 1), out var p) || p is < 1 or > 65535)
                return false;
            host = address[..colon];
            port = p;
        }

        return GameDig.IsValidHost(host);
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
