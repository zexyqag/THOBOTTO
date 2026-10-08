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
        if (c.Details is { } details)
            lines.Add($"**About:** {Quote(details)}");
        if (c.PardonedAt is { } pardoned)
            lines.Add($"**Pardoned** <t:{pardoned.ToUnixTimeSeconds()}:R> by <@{c.PardonedById}>{(c.PardonReason is { } why ? $": {why}" : "")}");

        return new()
        {
            Title = $"Case #{c.Number} · {Label(c.Type)}",
            Description = string.Join('\n', lines),
            Timestamp = c.CreatedAt,
            Color = new(c.PardonedAt is not null ? 0x99AAB5 : c.Type == CaseTypes.Note ? 0x5865F2 : 0xFEE75C),
        };
    }

    public static IEnumerable<IMessageComponentProperties> LogButtons(ModCase c)
        => c.Type == CaseTypes.Warn && c.PardonedAt is null
            ? [new ActionRowProperties { new ButtonProperties($"modpardon:{c.Number}", "Pardon", EmojiProperties.Standard("🕊️"), ButtonStyle.Secondary) }]
            : [];

    // One line in /mod history.
    public static string Line(ModCase c)
        => $"`#{c.Number}` {Label(c.Type)} <t:{c.CreatedAt.ToUnixTimeSeconds()}:d> by <@{c.ModeratorId}>: {Short(c.Reason ?? "–", 120)}"
            + (c.PardonedAt is not null ? " *(pardoned)*" : "");

    // What the member is told, or null for what they aren't (notes).
    public static string? ToMember(ModCase c, bool nameModerator)
    {
        var by = nameModerator ? $" by <@{c.ModeratorId}>" : "";
        return c.Type switch
        {
            CaseTypes.Warn => $"you were warned{by}. Reason: {c.Reason}",
            _ => null,
        };
    }

    public static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Quote(string text) => "\n> " + Short(text, 500).Replace("\n", "\n> ");
}
