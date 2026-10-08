using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.GameServers;
using THOBOTTO.Notifications;

namespace THOBOTTO.Games;

// Games, their role pickers, a DM topic per game, and the game and server lines on session
// messages. Upcoming sessions are re-rendered every few minutes so the server status stays fresh.
public sealed class GameDirectory(
    RestClient rest,
    IDbContextFactory<BotDbContext> dbFactory,
    ServerBoardService servers,
    IServiceProvider services,
    TimeProvider time,
    ILogger<GameDirectory> logger) : BackgroundService, INotificationTopicSource, IEventDecorator
{
    public const string ModuleId = "games";
    public const int MaxGames = 25;

    private static readonly TimeSpan StatusRefresh = TimeSpan.FromMinutes(5);

    public static string Topic(long gameId) => $"games.{gameId}";

    public static string PlayerCount(int players) => players == 1 ? "1 player" : $"{players} players";

    public static string Label(Game game) => game.Emoji is { } emoji ? $"{emoji} {game.Name}" : game.Name;

    public async Task<IReadOnlyList<Game>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Games.Where(g => g.GuildId == guildId).OrderBy(g => g.Name).ToListAsync();
    }

    public async Task<Game?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Games.FirstOrDefaultAsync(g => g.GuildId == guildId && g.Id == id);
    }

    public async Task<Game?> ByRoleAsync(ulong guildId, ulong roleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Games.FirstOrDefaultAsync(g => g.GuildId == guildId && g.RoleId == roleId);
    }

    public async Task<Game> AddAsync(Game game, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Games.Add(game);
        db.AuditEntries.Add(new() { GuildId = game.GuildId, ActorId = actorId, Action = "games.add", Details = $"{game.Name} role {game.RoleId}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await RenderPickersAsync(game.GuildId);
        return game;
    }

    public async Task<Game> UpdateAsync(long id, Action<Game> change, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var game = (await db.Games.FindAsync(id))!;
        change(game);
        db.AuditEntries.Add(new() { GuildId = game.GuildId, ActorId = actorId, Action = "games.edit", Details = $"{game.Name} players {game.Players?.ToString() ?? "any"}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await RenderPickersAsync(game.GuildId);
        return game;
    }

    public async Task<IReadOnlyList<GameMode>> ModesAsync(long gameId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.GameModes.Where(m => m.GameId == gameId).OrderBy(m => m.Players).ThenBy(m => m.Name).ToListAsync();
    }

    // Adds a mode, or changes its player count when the game has one by that name.
    public async Task<GameMode> SetModeAsync(Game game, string name, int players, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var mode = await db.GameModes.FirstOrDefaultAsync(m => m.GameId == game.Id && m.Name.ToLower() == name.ToLower());
        if (mode is null)
            db.GameModes.Add(mode = new() { GameId = game.Id, Name = name, Players = players });
        else
            mode.Players = players;
        db.AuditEntries.Add(new() { GuildId = game.GuildId, ActorId = actorId, Action = "games.mode", Details = $"{game.Name}: {name} {players}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return mode;
    }

    public async Task RemoveModeAsync(Game game, GameMode mode, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.GameModes.Where(m => m.Id == mode.Id).ExecuteDeleteAsync();
        db.AuditEntries.Add(new() { GuildId = game.GuildId, ActorId = actorId, Action = "games.mode.remove", Details = $"{game.Name}: {mode.Name}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
    }

    public async Task RemoveAsync(Game game, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Games.Where(g => g.Id == game.Id).ExecuteDeleteAsync();
        db.AuditEntries.Add(new() { GuildId = game.GuildId, ActorId = actorId, Action = "games.remove", Details = game.Name, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await RenderPickersAsync(game.GuildId);
    }

    public async Task<bool> ToggleRoleAsync(ulong guildId, ulong userId, Game game, bool? want = null)
    {
        var member = await rest.GetGuildUserAsync(guildId, userId);
        var has = member.RoleIds.Contains(game.RoleId);
        var give = want ?? !has;
        if (give && !has)
            await rest.AddGuildUserRoleAsync(guildId, userId, game.RoleId);
        else if (!give && has)
            await rest.RemoveGuildUserRoleAsync(guildId, userId, game.RoleId);
        return give;
    }

    public async Task AddPickerAsync(ulong guildId, ulong channelId)
    {
        var message = await rest.SendMessageAsync(channelId, await PickerAsync(guildId));
        await using var db = await dbFactory.CreateDbContextAsync();
        db.GamePickers.Add(new() { MessageId = message.Id, GuildId = guildId, ChannelId = channelId });
        await db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<NotificationTopic>> TopicsAsync(ulong guildId)
        => (await ListAsync(guildId)).Select(g => new NotificationTopic(Topic(g.Id), $"New {g.Name} sessions")).ToList();

    public async Task<IReadOnlyList<string>> LinesAsync(Event e)
    {
        if (e.GameId is not { } gameId || await FindAsync(e.GuildId, gameId) is not { } game)
            return [];

        var lines = new List<string> { $"🎮 {Label(game)}{(e.Mode is { } mode ? $" · {mode}" : "")} · <@&{game.RoleId}>" };
        if (game.ServerId is { } serverId)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.GameServers.FindAsync(serverId) is { } server)
            {
                lines.Add(servers.LastStatus(serverId) is { } status
                    ? $"🟢 Server online, {status.Players}/{status.MaxPlayers} players · `{server.Address}`"
                    : $"🔴 Server offline (or not checked yet) · `{server.Address}`");
            }
        }
        return lines;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StatusRefresh, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshSessionsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Refreshing game sessions failed");
            }
        }
    }

    // Sessions with a server, starting within a day or running now, so their status line stays current.
    private async Task RefreshSessionsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<long> sessions;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            sessions = await db.Events
                .Where(e => e.GameId != null && e.State == EventStates.Scheduled && e.StartsAt != null && e.StartsAt < now + TimeSpan.FromDays(1)
                    && db.Games.Any(g => g.Id == e.GameId && g.ServerId != null))
                .Select(e => e.Id)
                .ToListAsync(ct);
        }

        // Resolved late: EventBoard takes the decorators, this among them.
        var board = services.GetRequiredService<EventBoard>();
        foreach (var id in sessions)
            await board.RefreshAsync(id);
    }

    private async Task RenderPickersAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var pickers = await db.GamePickers.Where(p => p.GuildId == guildId).ToListAsync();
        var message = await PickerAsync(guildId);
        foreach (var picker in pickers)
        {
            try
            {
                await rest.ModifyMessageAsync(picker.ChannelId, picker.MessageId, m =>
                {
                    m.Embeds = message.Embeds;
                    m.Components = message.Components;
                });
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                db.GamePickers.Remove(picker);
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task<MessageProperties> PickerAsync(ulong guildId)
    {
        var games = await ListAsync(guildId);
        var rows = games
            .Select(g => new ButtonProperties($"gamerole:{g.Id}", g.Name.Length <= 80 ? g.Name : g.Name[..80], ButtonStyle.Secondary)
            {
                Emoji = g.Emoji is { } emoji ? EmojiProperties.Standard(emoji) : null,
            })
            .Chunk(5)
            .Select(chunk => (IMessageComponentProperties)new ActionRowProperties(chunk))
            .ToList();

        return new()
        {
            Embeds = [new()
            {
                Title = "Which games do you play?",
                Description = games.Count == 0
                    ? "No games yet."
                    : "Press a game to get its role and hear about its sessions; press again to drop it.",
                Color = new(0x5865F2),
            }],
            Components = rows,
        };
    }
}
