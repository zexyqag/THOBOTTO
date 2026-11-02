using NetCord.Rest;

namespace THOBOTTO.Access;

// The bot's application as Discord has it: its owners (the owner, or its team) and whether anyone may add it (Public
// Bot in the Developer Portal). Read again at most every 10 minutes.
public sealed class BotApplication(RestClient rest, TimeProvider time)
{
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);

    private (ulong[] Owners, bool Public, DateTimeOffset At)? _known;

    public async Task<IReadOnlyList<ulong>> OwnersAsync() => (await ReadAsync()).Owners;

    public async Task<bool> PublicAsync() => (await ReadAsync()).Public;

    private async Task<(ulong[] Owners, bool Public, DateTimeOffset At)> ReadAsync()
    {
        var now = time.GetUtcNow();
        if (_known is { } known && now - known.At < Kept)
            return known;
        var application = await rest.GetCurrentBotApplicationInformationAsync();
        ulong[] owners = application.Team is { } team ? [.. team.Users.Select(u => u.Id), team.OwnerId] : application.Owner is { } owner ? [owner.Id] : [];
        _known = known = (owners, application.BotPublic == true, now);
        return known;
    }
}
