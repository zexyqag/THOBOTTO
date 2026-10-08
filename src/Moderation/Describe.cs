using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Moderation;

// How cases read: in the log, in a history, and in the DM to the member.
public static class Describe
{
    public static string Label(string type) => type switch
    {
        CaseTypes.Warn => "⚠️ Warning",
        CaseTypes.Note => "📝 Note",
        CaseTypes.Timeout => "⏳ Timeout",
        CaseTypes.Untimeout => "⌛ Timeout lifted",
        CaseTypes.Kick => "👢 Kick",
        CaseTypes.Ban => "🔨 Ban",
        CaseTypes.Unban => "🔓 Unban",
        _ => type,
    };

    public static EmbedProperties Embed(ModCase c)
    {
        var lines = new List<string>
        {
            $"**Member:** <@{c.TargetId}> (`{c.TargetId}`)",
            $"**Moderator:** <@{c.ModeratorId}>",
            $"**Reason:** {c.Reason ?? "–"}",
        };
        if (c.EndsAt is { } ends)
            lines.Add($"**For:** {Durations.Format(ends - c.CreatedAt)}, until <t:{ends.ToUnixTimeSeconds()}:f>");
        else if (c.Type == CaseTypes.Ban)
            lines.Add("**For:** good");
        if (c.EndedAt is { } ended)
            lines.Add($"**Ended** <t:{ended.ToUnixTimeSeconds()}:R>");
        if (c.Details is { } details)
            lines.Add($"**About:** {Quote(details)}");
        if (c.PardonedAt is { } pardoned)
            lines.Add($"**Pardoned** <t:{pardoned.ToUnixTimeSeconds()}:R> by <@{c.PardonedById}>{(c.PardonReason is { } why ? $": {why}" : "")}");

        return new()
        {
            Title = $"Case #{c.Number} · {Label(c.Type)}",
            Description = string.Join('\n', lines),
            Timestamp = c.CreatedAt,
            Color = new(c.PardonedAt is not null || c.EndedAt is not null ? 0x99AAB5 : c.Type switch
            {
                CaseTypes.Note => 0x5865F2,
                CaseTypes.Warn or CaseTypes.Timeout => 0xFEE75C,
                CaseTypes.Kick or CaseTypes.Ban => 0xED4245,
                _ => 0x57F287,
            }),
        };
    }

    public static IEnumerable<IMessageComponentProperties> LogButtons(ModCase c)
        => c.Type == CaseTypes.Warn && c.PardonedAt is null
            ? [new ActionRowProperties { new ButtonProperties($"modpardon:{c.Number}", "Pardon", EmojiProperties.Standard("🕊️"), ButtonStyle.Secondary) }]
            : CaseTypes.LiftedBy(c.Type) is not null && c.EndedAt is null
                ? [new ActionRowProperties { new ButtonProperties($"modlift:{c.Number}", c.Type == CaseTypes.Ban ? "Unban" : "Lift", EmojiProperties.Standard("🔓"), ButtonStyle.Secondary) }]
                : [];

    // One line in /mod history.
    public static string Line(ModCase c)
        => $"`#{c.Number}` {Label(c.Type)} <t:{c.CreatedAt.ToUnixTimeSeconds()}:d> by <@{c.ModeratorId}>: {Short(c.Reason ?? "–", 120)}"
            + (c.EndsAt is { } ends ? $" ({Durations.Format(ends - c.CreatedAt)})" : "")
            + (c.PardonedAt is not null ? " *(pardoned)*" : c.EndedAt is not null && c.Type is CaseTypes.Ban or CaseTypes.Timeout ? " *(over)*" : "");

    // What the member is told, or null for what they aren't (notes).
    public static string? ToMember(ModCase c, bool nameModerator)
    {
        var by = nameModerator ? $" by <@{c.ModeratorId}>" : "";
        return c.Type switch
        {
            CaseTypes.Warn => $"you were warned{by}. Reason: {c.Reason}",
            CaseTypes.Timeout => $"you were timed out{by} until <t:{c.EndsAt!.Value.ToUnixTimeSeconds()}:f>. Reason: {c.Reason}",
            CaseTypes.Untimeout => $"your timeout was lifted{by}.",
            CaseTypes.Kick => $"you were kicked{by}. Reason: {c.Reason}",
            CaseTypes.Ban => $"you were banned{by}{(c.EndsAt is { } until ? $" until <t:{until.ToUnixTimeSeconds()}:f>" : "")}. Reason: {c.Reason}",
            _ => null,
        };
    }

    public static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Quote(string text) => "\n> " + Short(text, 500).Replace("\n", "\n> ");
}
