using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

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
    ServerDetails details,
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

    [SubSlashCommand("edit", "Change a game server's details (yours, or any with " + BotPermissions.ManageGameServers + "); none clears one")]
    public async Task<InteractionMessageProperties> EditAsync(
        [SlashCommandParameter(Description = "Game server", AutocompleteProviderType = typeof(ServerAutocomplete))] string server,
        [SlashCommandParameter(Description = "Name to show instead of the game server's own", MaxLength = ServerDetails.NameLength)] string? name = null,
        [SlashCommandParameter(Name = "join-address", Description = "What players connect to, when it isn't the address checked (host or host:port)", MaxLength = ServerDetails.AddressLength)] string? joinAddress = null,
        [SlashCommandParameter(Description = "The modpack it runs", MaxLength = ServerDetails.ModpackLength)] string? modpack = null,
        [SlashCommandParameter(Name = "modpack-version", Description = "The modpack's version", MaxLength = ServerDetails.VersionLength)] string? modpackVersion = null,
        [SlashCommandParameter(Name = "modpack-link", Description = "Where to get the modpack (https://…)", MaxLength = ServerDetails.LinkLength)] string? modpackLink = null,
        [SlashCommandParameter(Description = "A line for players, e.g. whitelist: ask an admin", MaxLength = ServerDetails.NoteLength)] string? note = null,
        [SlashCommandParameter(Description = "Its password", MaxLength = ServerDetails.PasswordLength)] string? password = null,
        [SlashCommandParameter(Name = "password-for", Description = "Who gets the password")] PasswordFor? passwordFor = null,
        [SlashCommandParameter(Name = "password-role", Description = "With password-for a role: which")] Role? passwordRole = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var found = long.TryParse(server, out var id) ? await db.GameServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id) : null;
        if (found is null || found.GuildId != GuildId)
            return Replies.Ephemeral("There's no such game server. Pick one from the list.");
        if (found.OwnerId != Context.User.Id && !await CanManageAsync())
            return Replies.Ephemeral($"Only <@{found.OwnerId}> or someone with `{BotPermissions.ManageGameServers}` can change that game server.");

        // Left out: as it is; none: cleared.
        static string? Or(string? given, string? now, int length) => given is null ? now : given.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ServerDetails.Clean(given, length);
        var join = Or(joinAddress, found.JoinAddress, ServerDetails.AddressLength);
        var link = Or(modpackLink, found.ModpackLink, ServerDetails.LinkLength);
        var shown = passwordFor switch { PasswordFor.Card => PasswordShown.Card, PasswordFor.Anyone => PasswordShown.Anyone, PasswordFor.Role => PasswordShown.Role, _ => found.PasswordFor };
        var role = passwordRole?.Id ?? found.PasswordRoleId;
        if (ServerDetails.Problem(join, link, shown, role) is { } problem)
            return Replies.Ephemeral(problem);
        await watch.UpdateAsync(found.Id, Context.User.Id, s =>
        {
            s.Name = Or(name, s.Name, ServerDetails.NameLength);
            s.JoinAddress = join;
            s.Modpack = Or(modpack, s.Modpack, ServerDetails.ModpackLength);
            s.ModpackVersion = Or(modpackVersion, s.ModpackVersion, ServerDetails.VersionLength);
            s.ModpackLink = link;
            s.Note = Or(note, s.Note, ServerDetails.NoteLength);
            if (password is not null)
                s.Password = details.Protect(Or(password, null, ServerDetails.PasswordLength));
            (s.PasswordFor, s.PasswordRoleId) = (shown, shown == PasswordShown.Role ? role : null);
        });
        return Replies.Ephemeral($"Changed {found.Name ?? found.Game} at `{found.Address}`.");
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
        var roles = ((GuildUser)Context.User).RoleIds;
        foreach (var s in servers)
        {
            var game = (await gameDig.FindGameAsync(s.Game))?.Name ?? s.Game;
            var status = watch.LastStatus(s.Id) is { } st ? $"🟢 {st.Players}/{st.MaxPlayers}" : "⚫ offline";
            lines.Add($"{status} · {s.Name ?? game}: {game} at `{s.JoinAt}`, added by <@{s.OwnerId}>");
            if (s.Modpack is { } modpack)
                lines.Add($"-# Modpack: {modpack}{(s.ModpackVersion is { } version ? $" {version}" : "")}{(s.ModpackLink is { } link ? $" · <{link}>" : "")}");
            if (details.PasswordOf(s) is { } secret)
                lines.Add(ServerDetails.MaySee(s, roles) ? $"-# Password: `{secret}`" : $"-# Password: for <@&{s.PasswordRoleId}>");
            if (s.Note is { } note)
                lines.Add($"-# {note}");
        }
        var text = string.Join('\n', lines);
        return Replies.Ephemeral(text.Length <= 2000 ? text : text[..1999] + "…");
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

public enum PasswordFor
{
    [SlashCommandChoice(Name = "Shown on its card")] Card,
    [SlashCommandChoice(Name = "Anyone, privately with the card's button")] Anyone,
    [SlashCommandChoice(Name = "A role's members, privately with the card's button")] Role,
}

// A card's 🔑 button: the password, privately, to whoever may have it.
public sealed class ServerPasswordButtons(ServerDetails details, IDbContextFactory<BotDbContext> dbFactory) : GuildButtonModule
{
    [ComponentInteraction("serverpassword")]
    public async Task<InteractionCallbackProperties> ShowAsync(long serverId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var server = await db.GameServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId && s.GuildId == GuildId);
        var answer = server is null || details.PasswordOf(server) is not { } password ? "That game server has no password now."
            : !ServerDetails.MaySee(server, ((GuildUser)Context.User).RoleIds) ? $"The password is for <@&{server.PasswordRoleId}>."
            : $"**{server.Name ?? server.Game}**: password `{password}` · join at `{server.JoinAt}`";
        return InteractionCallback.Message(new() { Content = answer, Flags = MessageFlags.Ephemeral, AllowedMentions = AllowedMentionsProperties.None });
    }
}

