using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using THOBOTTO.Archive;
using THOBOTTO.Data;
using THOBOTTO.Discord;

namespace THOBOTTO.Mischief.Bets;

// Every bet of the server with its stakes and what it paid out, for the archive's export.
public sealed class BetsExport(IDbContextFactory<BotDbContext> dbFactory) : IExportSource
{
    private const int Batch = 500;

    public string Name => "bets";

    public async Task WriteAsync(ulong guildId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        json.WriteStartArray();
        long after = 0;
        while (true)
        {
            var bets = await db.Bets.AsNoTracking().Where(b => b.GuildId == guildId && b.Id > after).OrderBy(b => b.Id).Take(Batch).ToListAsync();
            if (bets.Count == 0)
                break;
            var ids = bets.Select(b => b.Id).ToList();
            var stakes = (await db.BetStakes.AsNoTracking().Where(s => ids.Contains(s.BetId)).OrderBy(s => s.Id).ToListAsync()).ToLookup(s => s.BetId);
            var payouts = (await db.BetPayouts.AsNoTracking().Where(p => ids.Contains(p.BetId)).OrderBy(p => p.Id).ToListAsync()).ToLookup(p => p.BetId);
            foreach (var bet in bets)
            {
                json.WriteStartObject();
                json.WriteNumber("id", bet.Id);
                json.WriteId("channel", bet.ChannelId);
                json.WriteId("message", bet.MessageId);
                json.WriteId("creator", bet.CreatorId);
                json.WriteString("question", bet.Question);
                json.WriteStartArray("options");
                foreach (var option in bet.Options)
                    json.WriteStringValue(option);
                json.WriteEndArray();
                json.WriteString("createdAt", bet.CreatedAt);
                json.WriteString("closesAt", bet.ClosesAt);
                json.WriteString("state", bet.State);
                json.WriteNumber("winningOption", bet.WinningOption);
                json.WriteId("resolvedBy", bet.ResolvedById);
                json.WriteTime("resolvedAt", bet.ResolvedAt);
                json.WriteNumber("creatorCutPercent", bet.CreatorCutPercent);
                json.WriteNumber("houseCutPercent", bet.HouseCutPercent);
                json.WriteBoolean("creatorCanBet", bet.CreatorCanBet);
                json.WriteStartArray("stakes");
                foreach (var stake in stakes[bet.Id])
                {
                    json.WriteStartObject();
                    json.WriteId("member", stake.UserId);
                    json.WriteNumber("option", stake.Option);
                    json.WriteNumber("amount", stake.Amount);
                    json.WriteString("at", stake.CreatedAt);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("payouts");
                foreach (var payout in payouts[bet.Id])
                {
                    json.WriteStartObject();
                    json.WriteId("member", payout.UserId);
                    json.WriteNumber("amount", payout.Amount);
                    json.WriteString("kind", payout.Kind);
                    json.WriteBoolean("reversed", payout.Reversed);
                    json.WriteString("at", payout.CreatedAt);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            await json.FlushAsync();
            after = bets[^1].Id;
        }
        json.WriteEndArray();
    }
}
