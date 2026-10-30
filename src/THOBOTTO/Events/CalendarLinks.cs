using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Data;
using THOBOTTO.Panel;

namespace THOBOTTO.Events;

// The calendar links themselves: a server's and each member's, their tokens and choices, and where they are.
public sealed class CalendarLinks(IDbContextFactory<BotDbContext> dbFactory, PanelLinks links, TimeProvider time)
{
    public async Task<CalendarLink> ServerLinkAsync(ulong guildId, bool renew = false)
        => await LinkAsync(l => l.GuildId == guildId, () => new() { Token = NewToken(), GuildId = guildId, CreatedAt = time.GetUtcNow() }, renew);

    public async Task<CalendarLink> MemberLinkAsync(ulong userId, bool renew = false)
        => await LinkAsync(l => l.UserId == userId, () => new() { Token = NewToken(), UserId = userId, CreatedAt = time.GetUtcNow() }, renew);

    public static string ChoiceIn(CalendarLink link, ulong guildId)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(link.Choices)?.GetValueOrDefault(guildId.ToString(CultureInfo.InvariantCulture)) ?? CalendarChoices.Everything;

    public async Task SetChoiceAsync(ulong userId, ulong guildId, string choice)
    {
        await MemberLinkAsync(userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.CalendarLinks.FirstAsync(l => l.UserId == userId);
        var choices = JsonSerializer.Deserialize<Dictionary<string, string>>(link.Choices) ?? [];
        choices[guildId.ToString(CultureInfo.InvariantCulture)] = choice;
        link.Choices = JsonSerializer.Serialize(choices);
        await db.SaveChangesAsync();
    }

    public async Task SetReminderAsync(ulong userId, int? minutes)
    {
        await MemberLinkAsync(userId);
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.CalendarLinks.FirstAsync(l => l.UserId == userId);
        link.ReminderMinutes = minutes;
        await db.SaveChangesAsync();
    }

    // The link a calendar app subscribes to (https; webcal for apps that take that), or null without the panel's address.
    public async Task<(string Https, string Webcal)?> AddressOfAsync(CalendarLink link)
        => await links.AddressAsync() is { } address ? ($"{address}/calendar/{link.Token}.ics", $"webcal://{new Uri(address).Authority}/calendar/{link.Token}.ics") : null;

    // The page for adding one event by hand, or null without the panel's address.
    public async Task<string?> AddPageAsync(ulong guildId, long eventId)
        => await links.AddressAsync() is { } address ? $"{address}/calendar/{(await ServerLinkAsync(guildId)).Token}/{eventId}" : null;

    private async Task<CalendarLink> LinkAsync(Expression<Func<CalendarLink, bool>> which, Func<CalendarLink> make, bool renew)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.CalendarLinks.FirstOrDefaultAsync(which);
        if (link is null)
            db.CalendarLinks.Add(link = make());
        else if (renew)
            link.Token = NewToken();
        else
            return link;
        await db.SaveChangesAsync();
        return link;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');

}
