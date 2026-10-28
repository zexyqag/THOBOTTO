namespace THOBOTTO.Expressions;

// An emoji or sticker as Discord lists it, as far as the shelf cares.
public sealed record DiscordExpression(ulong Id, string Kind, string Name, string? Tag, ulong? CreatorId, bool Animated, string ContentType, DateTimeOffset CreatedAt);

// What a look at Discord's lists means for the shelf: live rows whose emoji or sticker is gone there, and
// member-made ones there the shelf doesn't know.
public sealed record ExpressionSyncPlan(IReadOnlyList<long> Gone, IReadOnlyList<DiscordExpression> Unknown);

public static class ExpressionSync
{
    public static ExpressionSyncPlan Plan(IReadOnlySet<ulong> onDiscord, IReadOnlyList<DiscordExpression> adoptable, IReadOnlyList<Expression> rows)
    {
        var gone = rows.Where(e => e.State == ExpressionStates.Live && e.DiscordId is { } id && !onDiscord.Contains(id)).Select(e => e.Id).ToList();
        var known = rows.Where(e => e.DiscordId is not null).Select(e => e.DiscordId!.Value).ToHashSet();
        return new(gone, adoptable.Where(a => !known.Contains(a.Id)).ToList());
    }
}
