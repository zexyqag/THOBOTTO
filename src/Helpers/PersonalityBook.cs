using System.Collections.Concurrent;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;

namespace THOBOTTO.Helpers;

// Each server's personalities and which helper wears which. Wearing one means the helper's
// nickname and avatar in that server follow it, and it speaks with its phrases.
public sealed class PersonalityBook(IDbContextFactory<BotDbContext> dbFactory, HelperFleet fleet, TimeProvider time, ILogger<PersonalityBook> logger) : IHelperAware
{
    public const int MaxAvatarBytes = 2 * 1024 * 1024;

    private readonly ConcurrentDictionary<(ulong Guild, ulong Helper), Personality?> _worn = new();

    public async Task<IReadOnlyList<Personality>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Personalities.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.Name).ToListAsync();
    }

    public async Task<Personality?> FindAsync(ulong guildId, long id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Personalities.AsNoTracking().FirstOrDefaultAsync(p => p.GuildId == guildId && p.Id == id);
    }

    public async Task<IReadOnlyDictionary<ulong, long>> AssignmentsAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.HelperAssignments.Where(a => a.GuildId == guildId).ToDictionaryAsync(a => a.HelperId, a => a.PersonalityId);
    }

    // A new personality, from a template or blank.
    public async Task<Personality> CreateAsync(ulong guildId, string name, Template? template, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var personality = new Personality
        {
            GuildId = guildId,
            Name = name,
            Color = template?.Color,
            Phrases = template?.Phrases.ToDictionary(p => p.Key, p => p.Value.ToList()) ?? [],
            CreatedAt = time.GetUtcNow(),
        };
        db.Personalities.Add(personality);
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "personality.create", Details = name, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        return personality;
    }

    // Changes a personality and shows it on every helper wearing it here.
    public async Task<Personality?> ChangeAsync(ulong guildId, long id, ulong actorId, string what, Action<Personality> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var personality = await db.Personalities.FirstOrDefaultAsync(p => p.GuildId == guildId && p.Id == id);
        if (personality is null)
            return null;
        change(personality);
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "personality.change", Details = $"{personality.Name}: {what}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        foreach (var helperId in await db.HelperAssignments.Where(a => a.GuildId == guildId && a.PersonalityId == id).Select(a => a.HelperId).ToListAsync())
            await ApplyAsync(guildId, helperId);
        return personality;
    }

    public async Task DeleteAsync(ulong guildId, long id, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var wearers = await db.HelperAssignments.Where(a => a.GuildId == guildId && a.PersonalityId == id).Select(a => a.HelperId).ToListAsync();
        await db.HelperAssignments.Where(a => a.GuildId == guildId && a.PersonalityId == id).ExecuteDeleteAsync();
        await db.Personalities.Where(p => p.GuildId == guildId && p.Id == id).ExecuteDeleteAsync();
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "personality.delete", Details = id.ToString(), CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        foreach (var helperId in wearers)
            await ApplyAsync(guildId, helperId);
    }

    // Puts a personality on a helper in a server; null takes it off.
    public async Task AssignAsync(ulong guildId, ulong helperId, long? personalityId, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var current = await db.HelperAssignments.FindAsync(guildId, helperId);
        if (personalityId is { } id)
        {
            if (current is null)
                db.HelperAssignments.Add(new() { GuildId = guildId, HelperId = helperId, PersonalityId = id });
            else
                current.PersonalityId = id;
        }
        else if (current is not null)
            db.HelperAssignments.Remove(current);
        db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "personality.assign", Details = $"{helperId} → {personalityId?.ToString() ?? "none"}", CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await ApplyAsync(guildId, helperId);
    }

    public async Task<Personality?> WornAsync(ulong guildId, ulong helperId)
    {
        if (_worn.TryGetValue((guildId, helperId), out var known))
            return known;
        await using var db = await dbFactory.CreateDbContextAsync();
        var worn = await db.HelperAssignments.Where(a => a.GuildId == guildId && a.HelperId == helperId)
            .Join(db.Personalities, a => a.PersonalityId, p => p.Id, (_, p) => p)
            .AsNoTracking()
            .FirstOrDefaultAsync();
        return _worn[(guildId, helperId)] = worn;
    }

    public async Task<string> NameAsync(ulong guildId, HelperBot helper) => (await WornAsync(guildId, helper.UserId))?.Name ?? helper.Name;

    public async Task<int> ColorAsync(ulong guildId, HelperBot helper) => (await WornAsync(guildId, helper.UserId))?.Color ?? Template.Plain.Color;

    // A line for the moment, with the values filled in; plain lines where the personality has none.
    public async Task<string> SayAsync(ulong guildId, HelperBot helper, string moment, IReadOnlyDictionary<string, string> values)
    {
        var worn = await WornAsync(guildId, helper.UserId);
        IReadOnlyList<string> options = worn?.Phrases.GetValueOrDefault(moment) is { Count: > 0 } own ? own : Template.Plain.Phrases.GetValueOrDefault(moment) ?? [""];
        var phrase = new StringBuilder(options[Random.Shared.Next(options.Count)]);
        phrase.Replace("{helper}", worn?.Name ?? helper.Name);
        foreach (var (key, value) in values)
            phrase.Replace($"{{{key}}}", value);
        return phrase.ToString();
    }

    public async Task AttachAsync(HelperBot helper)
    {
        helper.Gateway.GuildCreate += async args => await ApplyAsync(args.GuildId, helper.UserId);
        await Task.CompletedTask;
    }

    public Task DetachAsync(HelperBot helper) => Task.CompletedTask;

    // Shows the worn personality's name and avatar on the helper in that server (or its own, with none).
    private async Task ApplyAsync(ulong guildId, ulong helperId)
    {
        _worn.TryRemove((guildId, helperId), out _);
        if (fleet.Helpers.FirstOrDefault(h => h.UserId == helperId) is not { } helper || !helper.InGuild(guildId))
            return;
        var worn = await WornAsync(guildId, helperId);
        try
        {
            if (worn is not null)
            {
                await helper.Gateway.Rest.ModifyCurrentGuildUserAsync(guildId, u =>
                {
                    u.Nickname = worn.Name;
                    if (worn.Avatar is { } avatar)
                        u.Avatar = new ImageProperties(Format(worn.AvatarType), avatar, false);
                });
            }
            // NetCord leaves null fields out, but taking a nickname or avatar off needs an explicit null.
            if (worn?.Avatar is null)
                await ClearAsync(helper, guildId, worn is null ? "{\"nick\":null,\"avatar\":null}" : "{\"avatar\":null}");
        }
        catch (RestException ex)
        {
            // Without Change Nickname it keeps its own name; avatars are rate-limited by Discord.
            logger.LogDebug("Showing {Helper}'s personality in {GuildId} failed: {Message}", helper.Name, guildId, ex.Message);
        }
    }

    private static async Task ClearAsync(HelperBot helper, ulong guildId, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        await using var _ = await helper.Gateway.Rest.SendRequestAsync(HttpMethod.Patch, content, $"/guilds/{guildId}/members/@me", null, new(guildId, null!));
    }

    private static ImageFormat Format(string? type) => type switch
    {
        "image/png" => ImageFormat.Png,
        "image/gif" => ImageFormat.Gif,
        "image/webp" => ImageFormat.Webp,
        _ => ImageFormat.Jpeg,
    };
}
