using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using NodaTime;

using THOBOTTO.Data;
using THOBOTTO.Helpers;
using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Wrapped;

// A Wrapped to show: in Discord as an embed, in the panel as a card.
public sealed record WrappedCard(
    string Title,
    string? Intro,
    int? Color,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<(string Heading, IReadOnlyList<string> Lines)> Lists);

// Builds Wrapped for a server, a voice or a member from what the features add (IWrappedSection, IWrappedVoices).
// Only members who opted in are named in rankings; everyone counts in the totals.
public sealed class WrappedService(
    IEnumerable<IWrappedSection> sections,
    IEnumerable<IWrappedVoices> voices,
    ModuleState modules,
    SettingsStore settings,
    GatewayClient gateway,
    HelperFleet fleet,
    TimeZones zones,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : IMemberLeft
{
    public async Task<bool> IsInAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WrappedOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId);
    }

    public async Task SetInAsync(ulong guildId, ulong userId, bool include)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!include)
            await db.WrappedOptIns.Where(o => o.GuildId == guildId && o.UserId == userId).ExecuteDeleteAsync();
        else if (!await db.WrappedOptIns.AnyAsync(o => o.GuildId == guildId && o.UserId == userId))
        {
            db.WrappedOptIns.Add(new() { GuildId = guildId, UserId = userId, At = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
    }

    public Task MemberLeftAsync(ulong guildId, ulong userId) => SetInAsync(guildId, userId, false);

    // The periods that may be seen now, by the server's reveal setting.
    public async Task<IReadOnlyList<WrappedPeriod>> RevealedAsync(ulong guildId)
    {
        var rules = await settings.GetAsync<WrappedRules>(guildId, WrappedPoster.ModuleId);
        var (zone, _) = await zones.ForAsync(guildId, 0);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow()).InZone(zone);
        var yearOver = now.Month == 12 && now.Day == 31 && now.Hour >= 12;
        return [.. Enum.GetValues<WrappedPeriod>().Where(period => (rules.Reveal, period) switch
        {
            (WrappedReveal.Anytime, _) => true,
            (_, WrappedPeriod.AllTime) => false,
            (_, WrappedPeriod.ThisYear) => yearOver,
            (WrappedReveal.Ended, WrappedPeriod.ThisMonth) => false,
            _ => true,
        })];
    }

    // Why a period can't be seen yet, or null.
    public async Task<string?> HiddenAsync(ulong guildId, WrappedPeriod period)
        => (await RevealedAsync(guildId)).Contains(period) ? null : period switch
        {
            WrappedPeriod.ThisYear => "This year's Wrapped is revealed at noon on December 31st.",
            WrappedPeriod.ThisMonth => "This month's Wrapped is revealed when the month is over.",
            _ => "All time Wrapped isn't shown here, to keep this year's for its reveal.",
        };

    // Voices to pick from: "p:<personality>" or "h:<helper id>", with their names.
    public async Task<IReadOnlyList<(string Key, string Name)>> VoicesAsync(ulong guildId)
    {
        List<(string, string)> all = [];
        foreach (var source in voices)
            all.AddRange(await source.VoicesAsync(guildId));
        return all;
    }

    public Task<IReadOnlyList<WrappedCard>> ServerAsync(Guild guild, WrappedPeriod period, Func<ulong, string> name)
        => CardsAsync(guild, null, null, period, name);

    // Owner: how the titles name them ("Your", "Ana's").
    public Task<IReadOnlyList<WrappedCard>> MemberAsync(Guild guild, ulong userId, string owner, WrappedPeriod period, Func<ulong, string> name)
        => CardsAsync(guild, userId, owner, period, name);

    public async Task<WrappedCard?> VoiceAsync(Guild guild, string key, WrappedPeriod period, Func<ulong, string> name)
    {
        var ask = await AskAsync(guild, null, period);
        foreach (var source in voices)
        {
            if (await source.VoiceAsync(ask, key) is not { } voice)
                continue;
            var title = $"🎙️ {voice.Name}'s Wrapped · {ask.Span.Label}";
            return voice.Part is { } part
                ? Card(title, voice.Intro, voice.Color, [part], await NamedAsync(guild.Id), name)
                : new(title, $"No music yet for {ask.Span.Label}.", voice.Color, [], []);
        }
        return null;
    }

    // Each card from the sections that have something for it, those of modules turned off left out; a card with
    // nothing is dropped, and with nothing at all the music card says so.
    private async Task<IReadOnlyList<WrappedCard>> CardsAsync(Guild guild, ulong? userId, string? owner, WrappedPeriod period, Func<ulong, string> name)
    {
        var ask = await AskAsync(guild, userId, period);
        var named = await NamedAsync(guild.Id);
        List<WrappedCard> cards = [];
        foreach (var kind in Enum.GetValues<WrappedCardKind>())
        {
            List<WrappedPart> parts = [];
            foreach (var section in sections.Where(s => s.Card == kind).OrderBy(s => s.Order))
            {
                if (await modules.IsEnabledAsync(guild.Id, section.ModuleId) && await section.PartAsync(ask) is { } part)
                    parts.Add(part);
            }
            if (parts.Count > 0)
                cards.Add(Card(Title(kind, guild, owner, ask.Span), Intro(parts, owner), null, parts, named, name));
        }
        return cards.Count > 0 ? cards : [new(Title(WrappedCardKind.Music, guild, owner, ask.Span), $"No music yet for {ask.Span.Label}.", null, [], [])];
    }

    private static string Title(WrappedCardKind card, Guild guild, string? owner, WrappedSpan span) => (card, owner) switch
    {
        (WrappedCardKind.Music, null) => $"🎧 {guild.Name} Wrapped",
        (WrappedCardKind.Music, _) => $"✨ {owner} Wrapped",
        (_, null) => $"💬 {guild.Name} on Discord",
        _ => $"💬 {owner} Discord",
    } + $" · {span.Label}";

    // The parts' phrases as the card's first line: "12 messages, 3 hours in voice."; in a member's own, "You sent 12
    // messages and spent 3 hours in voice."
    public static string? Intro(IReadOnlyList<WrappedPart> parts, string? owner)
    {
        var phrases = parts.Select(p => p.Intro).OfType<string>().ToList();
        if (phrases.Count == 0)
            return null;
        if (owner is null)
            return string.Join(", ", phrases) + ".";
        return $"You {(phrases.Count == 1 ? phrases[0] : $"{string.Join(", ", phrases[..^1])} and {phrases[^1]}")}.";
    }

    // Rankings name members only if they asked to be; lists left empty are dropped.
    public static WrappedCard Card(string title, string? intro, int? color, IReadOnlyList<WrappedPart> parts, IReadOnlySet<ulong> named, Func<ulong, string> name)
        => new(title, intro, color,
            [.. parts.SelectMany(p => p.Facts)],
            [.. parts.SelectMany(p => p.Lists).Select(l => (l.Heading, Lines(l, named, name))).Where(l => l.Item2.Count > 0)]);

    private static IReadOnlyList<string> Lines(WrappedList list, IReadOnlySet<ulong> named, Func<ulong, string> name)
        => [.. list.Lines.Where(l => l.Member is not { } member || named.Contains(member)).Take(list.Most)
            .Select((l, i) => $"{i + 1}. {(l.Member is { } member ? name(member) : l.Name)}{(l.Detail is { } detail ? $" · {detail}" : "")}")];

    private async Task<WrappedAsk> AskAsync(Guild guild, ulong? userId, WrappedPeriod period)
    {
        var (zone, _) = await zones.ForAsync(guild.Id, userId ?? 0);
        var span = WrappedSpan.For(period, zone, Instant.FromDateTimeOffset(time.GetUtcNow()));
        return new(guild.Id, span, zone, Bots(), userId, id => guild.Channels.TryGetValue(id, out var channel) ? $"#{channel.Name}" : "a deleted channel");
    }

    private async Task<HashSet<ulong>> NamedAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.WrappedOptIns.Where(o => o.GuildId == guildId).Select(o => o.UserId).ToListAsync()).ToHashSet();
    }

    // Messages from the bot itself and its helpers aren't anyone's activity.
    private HashSet<ulong> Bots() => [.. fleet.Helpers.Select(h => h.UserId), .. gateway.Cache.User is { } bot ? [bot.Id] : Array.Empty<ulong>()];
}
