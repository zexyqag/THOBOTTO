using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;

namespace THOBOTTO.Helpers;

// Each server's personalities and which helper wears which. Wearing one means the helper's
// nickname and avatar in that server follow it, and it speaks with its phrases; where it may not
// post, the main bot posts for it under that name.
public sealed class PersonalityBook(IDbContextFactory<BotDbContext> dbFactory, HelperFleet fleet, RestClient rest, TimeProvider time, ILogger<PersonalityBook> logger) : IHelperAware, IHostedService
{
    public const int MaxAvatarBytes = 2 * 1024 * 1024;

    // Before the helpers connect: a personality that is still an unchanged copy of a template takes the template's
    // avatar when it has none, so the ones made before the templates had avatars catch up.
    public async Task StartAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var filled = 0;
        foreach (var personality in await db.Personalities.Where(p => p.Avatar == null).ToListAsync(ct))
        {
            if (PersonalityFile.Templates.FirstOrDefault(t => t.Name == personality.Name) is not { AvatarData: { } avatar } template || !SameLines(template, personality))
                continue;
            (personality.Avatar, personality.AvatarType) = (avatar.Bytes, avatar.Type);
            filled++;
        }
        if (filled > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("{Count} personalities still copies of a template took the template's avatar", filled);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static bool SameLines(PersonalityFile template, Personality personality)
        => template.Lines.Count == personality.Phrases.Count
            && template.Lines.All(m => personality.Phrases.TryGetValue(m.Key, out var lines) && lines.SequenceEqual(m.Value));

    private readonly ConcurrentDictionary<(ulong Guild, ulong Helper), Personality?> _worn = new();

    // How long after a refused show (Discord limits avatar changes) the next try waits, try by try; then it's left.
    public static readonly TimeSpan[] Retries = [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)];
    private readonly ConcurrentDictionary<(ulong Guild, ulong Helper), int> _refused = new();

    public async Task<IReadOnlyList<Personality>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Personalities.AsNoTracking().Where(p => p.GuildId == guildId).OrderBy(p => p.Name).ToListAsync();
    }

    // Every personality that would rather speak through a particular engine, across servers.
    public async Task<IReadOnlyList<(ulong GuildId, string Name, string Engine)>> PreferringAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await PreferringAsync(db);
    }

    public static async Task<IReadOnlyList<(ulong GuildId, string Name, string Engine)>> PreferringAsync(BotDbContext db)
        => [.. (await db.Personalities.AsNoTracking().Where(p => p.Engine != null).OrderBy(p => p.Name).Select(p => new { p.GuildId, p.Name, p.Engine }).ToListAsync())
            .Select(p => (p.GuildId, p.Name, p.Engine!))];

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

    // A new personality: blank, or from a template, an imported file or another server's.
    public async Task<Personality> CreateAsync(ulong guildId, string name, PersonalityFile? from, ulong actorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var avatar = from?.AvatarData;
        var personality = new Personality
        {
            GuildId = guildId,
            Name = name,
            Color = from?.ColorValue,
            Avatar = avatar?.Bytes,
            AvatarType = avatar?.Type,
            Phrases = from?.Lines.ToDictionary(p => p.Key, p => p.Value.ToList()) ?? [],
            Voices = from?.Voices is { } voices ? new(voices) : [],
            Effects = from?.Effects is { } effects ? new(effects) : [],
            Engine = from?.Engine,
            Filters = from?.Filters,
            AlsoAnswersTo = from?.AlsoAnswersTo is { } also ? [.. also] : [],
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

    // What a helper answers to in voice: its name, and what its personality also answers to.
    public async Task<IReadOnlyList<string>> AnswersToAsync(ulong guildId, HelperBot helper)
        => await WornAsync(guildId, helper.UserId) is { } worn ? [worn.Name, .. worn.AlsoAnswersTo] : [helper.Name];

    public async Task<int> ColorAsync(ulong guildId, HelperBot helper) => (await WornAsync(guildId, helper.UserId))?.Color ?? PersonalityFile.Plain.ColorValue!.Value;

    // A line for the moment, with the values filled in; plain lines where the personality has none. Said with its
    // voice marks, shown without.
    public async Task<HelperLine> SayAsync(ulong guildId, HelperBot helper, string moment, IReadOnlyDictionary<string, string> values)
    {
        var worn = await WornAsync(guildId, helper.UserId);
        IReadOnlyList<string> options = worn?.Phrases.GetValueOrDefault(moment) is { Count: > 0 } own ? own : PersonalityFile.Plain.Lines.GetValueOrDefault(moment) ?? [""];
        var phrase = new StringBuilder(options[Random.Shared.Next(options.Count)]);
        phrase.Replace("{helper}", worn?.Name ?? helper.Name);
        foreach (var (key, value) in values)
            phrase.Replace($"{{{key}}}", value);
        return new(phrase.ToString());
    }

    // A line from the helper in a channel (e.g. a voice channel's chat), as it posts everything else.
    public Task ReplyAsync(ulong guildId, HelperBot helper, ulong channelId, string content)
        => PostAsync(guildId, helper, channelId, new() { Content = content, AllowedMentions = AllowedMentionsProperties.None });

    // Posts as the helper; where it may not post, the main bot posts for it, under its name.
    public async Task<(ulong Id, bool ByHelper)?> PostAsync(ulong guildId, HelperBot helper, ulong channelId, MessageProperties message)
    {
        // A helper's own chatter (now playing, joined, left) shows without notifying anyone.
        message.Flags = (message.Flags ?? 0) | MessageFlags.SuppressNotifications;
        try
        {
            return ((await helper.Gateway.Rest.SendMessageAsync(channelId, message)).Id, true);
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            var name = await NameAsync(guildId, helper);
            message.Content = message.Content is { } content ? $"**{name}:** {content}" : $"**{name}**";
        }

        try
        {
            return ((await rest.SendMessageAsync(channelId, message)).Id, false);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Posting to {ChannelId} in {GuildId} failed: {Message}", channelId, guildId, ex.Message);
            return null;
        }
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
            _refused.TryRemove((guildId, helperId), out _);
        }
        catch (RestException ex)
        {
            // Without Change Nickname it keeps its own name; Discord refuses avatars changed too often. Tried again
            // a few times, further apart each time, so a refused avatar shows without a restart.
            var refused = _refused.AddOrUpdate((guildId, helperId), 1, (_, n) => n + 1);
            if (refused <= Retries.Length)
            {
                var wait = Retries[refused - 1];
                logger.LogInformation("Showing {Helper}'s personality in {GuildId} failed ({Message}); trying again in {Minutes} min", helper.Name, guildId, ex.Message, (int)wait.TotalMinutes);
                _ = RetryAsync(guildId, helperId, wait);
            }
            else
            {
                logger.LogWarning("Showing {Helper}'s personality in {GuildId} failed again ({Message}); left until it's changed or the bot restarts", helper.Name, guildId, ex.Message);
            }
        }
    }

    private async Task RetryAsync(ulong guildId, ulong helperId, TimeSpan wait)
    {
        await Task.Delay(wait, time);
        await ApplyAsync(guildId, helperId);
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
