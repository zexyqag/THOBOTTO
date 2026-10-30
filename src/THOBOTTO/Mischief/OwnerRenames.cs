using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

// Renames of the server owner, whom bots can't rename: the rename is asked of them and their nickname watched; the
// moment it matches, the rename is in place and counts from then. A day without it, and the asker has the points
// back while the rename stays recorded as declined. Swept every few minutes, so a day that passed while the bot
// was down is caught up too.
public sealed class OwnerRenames(PointsEngine points, GatewayClient gateway, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time, ILogger<OwnerRenames> logger) : BackgroundService
{
    public static readonly TimeSpan Patience = TimeSpan.FromDays(1);
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(5);

    public async Task OnMemberAsync(GuildUser user)
    {
        if (!gateway.Cache.Guilds.TryGetValue(user.GuildId, out var guild) || guild.OwnerId != user.Id)
            return;
        await using var db = await dbFactory.CreateDbContextAsync();
        foreach (var rename in await MarkDoneAsync(db, user.GuildId, user.Id, user.Nickname, time.GetUtcNow()))
            logger.LogInformation("The owner of {GuildId} took the name {ActorId} gave them", user.GuildId, rename.ActorId);
    }

    // The asked renames of the owner whose name is now in place are done from now.
    public static async Task<IReadOnlyList<Rename>> MarkDoneAsync(BotDbContext db, ulong guildId, ulong ownerId, string? nickname, DateTimeOffset now)
    {
        var asked = await db.Renames
            .Where(r => r.GuildId == guildId && r.TargetId == ownerId && r.DoneAt == null && r.DeclinedAt == null && r.NewName == nickname)
            .ToListAsync();
        foreach (var rename in asked)
            rename.DoneAt = now;
        await db.SaveChangesAsync();
        return asked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Sweep, time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sweeping asked renames failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Asked renames a day old are declined, and what they cost goes back to the asker.
    private async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cutoff = now - Patience;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var overdue = await db.Renames.Where(r => r.DoneAt == null && r.DeclinedAt == null && r.CreatedAt <= cutoff).ToListAsync(ct);
        if (overdue.Count == 0)
            return;
        foreach (var rename in overdue)
            rename.DeclinedAt = now;
        await db.SaveChangesAsync(ct);
        foreach (var rename in overdue)
        {
            if (rename.Cost > 0)
                await points.RefundAsync(rename.GuildId, rename.ActorId, rename.Cost, $"rename {rename.TargetId} not taken by the owner");
            logger.LogInformation("The owner of {GuildId} didn't take the name {ActorId} gave them within a day", rename.GuildId, rename.ActorId);
        }
    }
}

public sealed class OwnerRenameHandler(OwnerRenames renames) : IGuildUserUpdateGatewayHandler
{
    public ValueTask HandleAsync(GuildUser arg) => new(renames.OnMemberAsync(arg));
}
