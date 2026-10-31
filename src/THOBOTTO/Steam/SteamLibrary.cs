using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;

namespace THOBOTTO.Steam;

public sealed record SteamOwner(ulong UserId, int Minutes);

public sealed record SharedGame(int AppId, string Name, long Minutes);

// Members' Steam accounts and the games they own, read again once a day.
public sealed class SteamLibrary(IDbContextFactory<BotDbContext> dbFactory, SteamClient client, TimeProvider time, ILogger<SteamLibrary> logger) : BackgroundService
{
    public const string ModuleId = "steam";
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(1);
    private static readonly TimeSpan StorePageKept = TimeSpan.FromDays(30);

    // One read at a time: a member's games are replaced whole.
    private readonly SemaphoreSlim _reading = new(1, 1);

    public async Task<SteamLink?> FindAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SteamLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId);
    }

    public async Task<int> CountAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SteamGames.CountAsync(g => g.UserId == userId);
    }

    // Linked anew (another account replaces the last), and read at once.
    public async Task<string?> LinkAsync(ulong userId, ulong steamId)
    {
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var link = await db.SteamLinks.FindAsync(userId);
            if (link is null)
                db.SteamLinks.Add(link = new() { UserId = userId, Name = steamId.ToString() });
            else if (link.SteamId != steamId)
                await db.SteamGames.Where(g => g.UserId == userId).ExecuteDeleteAsync();
            (link.SteamId, link.LinkedAt, link.ReadAt, link.Hidden) = (steamId, time.GetUtcNow(), null, false);
            await db.SaveChangesAsync();
        }
        return await ReadAsync(userId);
    }

    public async Task UnlinkAsync(ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.SteamGames.Where(g => g.UserId == userId).ExecuteDeleteAsync();
        await db.SteamLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync();
    }

    // Their name and games from Steam; null when read, else why not.
    public async Task<string?> ReadAsync(ulong userId, CancellationToken ct = default)
    {
        if (!client.Configured)
            return "Steam isn't set up: the bot's owner sets a Steam Web API key on the panel's Integrations page.";
        await _reading.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.SteamLinks.FindAsync([userId], ct) is not { } link)
                return "That Steam account isn't linked any more.";
            var player = await client.PlayerAsync(link.SteamId, ct);
            var games = await client.GamesAsync(link.SteamId, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.SteamGames.Where(g => g.UserId == userId).ExecuteDeleteAsync(ct);
            db.SteamGames.AddRange((games ?? []).DistinctBy(g => g.AppId).Select(g => new SteamGame { UserId = userId, AppId = g.AppId, Name = g.Name, Minutes = g.Minutes }));
            (link.Name, link.ReadAt, link.Hidden) = (player?.Name ?? link.Name, time.GetUtcNow(), games is null);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Reading <@{UserId}>'s Steam games failed: {Message}", userId, ex.Message);
            return ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }
                ? "Steam refused the bot's Web API key: the bot's owner checks it on the panel's Integrations page."
                : $"Steam didn't answer: {ex.Message}";
        }
        finally
        {
            _reading.Release();
        }
    }

    public async Task<IReadOnlyList<SteamLink>> LinkedAsync(IReadOnlyCollection<ulong> userIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SteamLinks.AsNoTracking().Where(l => userIds.Contains(l.UserId)).ToListAsync();
    }

    // The game's name, and who of these own it, most played first.
    public async Task<(string? Name, IReadOnlyList<SteamOwner> Owners)> OwnersAsync(int appId, IReadOnlyCollection<ulong> userIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var owners = await db.SteamGames.AsNoTracking().Where(g => g.AppId == appId && userIds.Contains(g.UserId)).OrderByDescending(g => g.Minutes).ToListAsync();
        var name = owners.FirstOrDefault()?.Name ?? await db.SteamGames.Where(g => g.AppId == appId).Select(g => g.Name).FirstOrDefaultAsync();
        return (name, [.. owners.Select(g => new SteamOwner(g.UserId, g.Minutes))]);
    }

    // For picking one: the games these own whose names have the text, those most of them own first.
    public async Task<IReadOnlyList<(int AppId, string Name)>> GamesAsync(IReadOnlyCollection<ulong> userIds, string text, int take)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.SteamGames.AsNoTracking()
            .Where(g => userIds.Contains(g.UserId) && EF.Functions.ILike(g.Name, $"%{text}%"))
            .GroupBy(g => g.AppId)
            .Select(g => new { AppId = g.Key, Name = g.Max(x => x.Name), Owners = g.Count(), Minutes = g.Sum(x => (long)x.Minutes) })
            .OrderByDescending(g => g.Owners).ThenByDescending(g => g.Minutes)
            .Take(take)
            .ToListAsync())
            .Select(g => (g.AppId, g.Name ?? "")).ToList();
    }

    // The games all of these own, the most played between them first.
    public async Task<IReadOnlyList<SharedGame>> SharedAsync(IReadOnlyCollection<ulong> userIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.SteamGames.AsNoTracking()
            .Where(g => userIds.Contains(g.UserId))
            .GroupBy(g => g.AppId)
            .Where(g => g.Count() == userIds.Count)
            .Select(g => new { AppId = g.Key, Name = g.Max(x => x.Name), Minutes = g.Sum(x => (long)x.Minutes) })
            .OrderByDescending(g => g.Minutes)
            .ToListAsync())
            .Select(g => new SharedGame(g.AppId, g.Name ?? "", g.Minutes)).ToList();
    }

    // Whether it's played together, as its store page says; the store is asked at most once a month for each game.
    public async Task<bool?> TogetherAsync(int appId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.SteamApps.FindAsync([appId], ct);
        if (known is not null && time.GetUtcNow() - known.CheckedAt < StorePageKept)
            return known.Together;
        bool? together;
        try
        {
            together = await client.TogetherAsync(appId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            logger.LogDebug("Steam's store page for {AppId} couldn't be read: {Message}", appId, ex.Message);
            return known?.Together;
        }
        if (known is null)
            db.SteamApps.Add(known = new() { AppId = appId });
        (known.Together, known.CheckedAt) = (together, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return together;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick, time);
        do
        {
            if (!client.Configured)
                continue;
            try
            {
                List<ulong> due;
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                {
                    var before = time.GetUtcNow() - Fresh;
                    due = await db.SteamLinks.Where(l => l.ReadAt == null || l.ReadAt < before).Select(l => l.UserId).ToListAsync(ct);
                }
                foreach (var userId in due)
                    await ReadAsync(userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reading Steam games failed");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
