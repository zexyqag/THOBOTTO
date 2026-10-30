using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Data;

namespace THOBOTTO.Mischief;

// Creates the colour roles behind /name colour and deletes them when paints end. A sweep runs every
// minute, so paints that ended while the bot was down are cleaned up too.
public sealed class PaintRoles(
    RestClient rest,
    GatewayClient gateway,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<PaintRoles> logger) : BackgroundService
{
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(1);

    // Creates the role just under the bot's own top role, so its colour wins over the
    // member's other roles, and gives it to the member. Null if Discord refused.
    public async Task<ulong?> ApplyAsync(ulong guildId, ulong userId, Color colour, string name)
    {
        Role? role = null;
        try
        {
            role = await rest.CreateGuildRoleAsync(guildId, new() { Name = $"🎨 {name}", Colors = new(colour) });

            var roles = await rest.GetGuildRolesAsync(guildId);
            var bot = await rest.GetGuildUserAsync(guildId, gateway.Cache.User!.Id);
            var botTop = roles.Where(r => bot.RoleIds.Contains(r.Id)).MaxBy(r => r.Position)!;

            // Moving one role onto a taken position ties, and Discord breaks ties by age, so the
            // new role would lose. Send the whole order below the bot instead, paint first.
            var below = roles
                .Where(r => r.Position < botTop.Position && r.Id != guildId && r.Id != role.Id)
                .OrderByDescending(r => r.Position)
                .ToList();
            var order = below.Prepend(role).ToList();
            await rest.ModifyGuildRolePositionsAsync(guildId, order.Select((r, i) => new RolePositionProperties(r.Id) { Position = order.Count - i }));

            await rest.AddGuildUserRoleAsync(guildId, userId, role.Id);
            return role.Id;
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden)
        {
            logger.LogInformation(ex, "Painting {UserId} in {GuildId} was refused", userId, guildId);
            if (role is not null)
                await DeleteRoleAsync(guildId, role.Id);
            return null;
        }
    }

    // Deletes the paint's role and marks it cleaned up. The caller saves.
    public async Task RemoveAsync(MischiefEffect paint)
    {
        if (paint.RoleId is { } roleId)
            await DeleteRoleAsync(paint.GuildId, roleId);
        paint.RoleId = null;
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
                logger.LogError(ex, "Sweeping ended paints failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ended = await db.MischiefEffects
            .Where(e => e.Kind == MischiefEffectKinds.Paint && e.RoleId != null && e.EndsAt <= now)
            .ToListAsync(ct);

        foreach (var paint in ended)
            await RemoveAsync(paint);

        await db.SaveChangesAsync(ct);
    }

    private async Task DeleteRoleAsync(ulong guildId, ulong roleId)
    {
        try
        {
            await rest.DeleteGuildRoleAsync(guildId, roleId);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}
