namespace THOBOTTO.Modules;

// What something costs in a server's points, as a feature tells it ("Proposing an emoji", "10 points, refunded if accepted").
public sealed record Price(string What, string Amount);

// A feature with prices to show on the points page and with /points prices.
public interface IPriceSource
{
    Task<IReadOnlyList<Price>> PricesAsync(ulong guildId);
}
