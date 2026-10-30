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
        CaseTypes.Purge => "🧹 Purge",
        CaseTypes.Slowmode => "🐢 Slowmode",
        CaseTypes.Lock => "🔒 Lock",
        CaseTypes.Unlock => "🔓 Unlock",
        CaseTypes.Move => "↪️ Moved",
        CaseTypes.Disconnect => "📴 Disconnected",
        CaseTypes.Mute => "🔇 Server mute",
        CaseTypes.Unmute => "🔈 Unmuted",
        CaseTypes.Deafen => "🙉 Server deafen",
        CaseTypes.Undeafen => "👂 Undeafened",
        CaseTypes.RoleAdd => "➕ Role given",
        CaseTypes.RoleRemove => "➖ Role taken",
        CaseTypes.AutoMod => "🛡️ Blocked by AutoMod",
        _ => type,
    };

    public static EmbedProperties Embed(ModCase c)
    {
        var lines = new List<string>();
        if (c.TargetId != c.ChannelId)
            lines.Add($"**Member:** <@{c.TargetId}> (`{c.TargetId}`)");
        if (c.ChannelId is { } channel)
            lines.Add($"**Channel:** <#{channel}>");
        if (c.RoleId is { } role)
            lines.Add($"**Role:** <@&{role}>");
        lines.Add($"**Moderator:** {Moderator(c)}");
        lines.Add($"**Reason:** {c.Reason ?? "–"}");
        if (c.EndsAt is { } ends)
            lines.Add($"**For:** {Durations.Format(ends - c.CreatedAt)}, until <t:{ends.ToUnixTimeSeconds()}:f>");
        else if (c.Type == CaseTypes.Ban)
            lines.Add("**For:** good");
        else if (CaseTypes.LiftedBy(c.Type) is not null && c.EndedAt is null)
            lines.Add("**For:** until lifted");
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
                CaseTypes.Purge or CaseTypes.Slowmode or CaseTypes.Lock => 0xEB459E,
                _ => 0x57F287,
            }),
        };
    }

    public static IEnumerable<IMessageComponentProperties> LogButtons(ModCase c)
        => c.Type == CaseTypes.Warn && c.PardonedAt is null
            ? [new ActionRowProperties { new ButtonProperties($"modpardon:{c.Number}", "Pardon", EmojiProperties.Standard("🕊️"), ButtonStyle.Secondary) }]
            : CaseTypes.LiftedBy(c.Type) is not null && c.EndedAt is null
                ? [new ActionRowProperties { new ButtonProperties($"modlift:{c.Number}", c.Type switch { CaseTypes.Ban => "Unban", CaseTypes.Lock => "Unlock", CaseTypes.RoleAdd => "Take back", CaseTypes.RoleRemove => "Give back", _ => "Lift" }, EmojiProperties.Standard("🔓"), ButtonStyle.Secondary) }]
                : [];

    // One line in /mod history.
    public static string Line(ModCase c)
        => $"`#{c.Number}` {Label(c.Type)} <t:{c.CreatedAt.ToUnixTimeSeconds()}:d> by {Moderator(c)}: {Short(c.Reason ?? "–", 120)}"
            + (c.EndsAt is { } ends ? $" ({Durations.Format(ends - c.CreatedAt)})" : "")
            + (c.PardonedAt is not null ? " *(pardoned)*" : c.EndedAt is not null && CaseTypes.LiftedBy(c.Type) is not null ? " *(over)*" : "");

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

    public static string Moderator(ModCase c) => c.ModeratorId == CaseTypes.AutoModId ? "Discord AutoMod" : $"<@{c.ModeratorId}>";

    public static string Short(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string Quote(string text) => "\n> " + Short(text, 500).Replace("\n", "\n> ");
}
