using System.Net.WebSockets;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Points;
using THOBOTTO.Voice.Speaking;

namespace THOBOTTO.Voice.Lines;

// Members' lines: "Ana says: …" or "Ana asks: …", said by the helper in their voice channel (one playing or
// listening there), else by a free one that hops in; for points and with a wait between a member's lines.
public sealed class HelperLines(HelperVoices voices, HelperSpeech speech, VoiceMouths mouths, SpeechClips clips, HelperHops hops, ModuleState modules, SettingsStore settings, PointsEngine points, TimeProvider time, ILogger<HelperLines> logger)
{
    public const int MostChars = 200;

    private readonly Cooldowns _cooldowns = new();

    public async Task<string> SayAsync(ulong guildId, GuildUser member, ulong channelId, string text, bool asks)
    {
        if (!await modules.IsEnabledAsync(guildId, HelperVoices.ModuleId))
            return ModuleRegistry.Off(HelperVoices.ModuleId);
        var rules = await settings.GetAsync<SpeechRules>(guildId, HelperVoices.ModuleId);
        if (!rules.Lines)
            return "Helpers don't say members' lines here.";
        if (!voices.On)
            return "Helper voices are off on this bot.";
        text = text.Trim();
        if (text.Length is 0 or > MostChars)
            return $"A line is 1 to {MostChars} characters.";
        var now = time.GetUtcNow();
        var wait = _cooldowns.Left(guildId, member.Id, now, TimeSpan.FromSeconds(rules.LineCooldownSeconds));
        if (wait > TimeSpan.Zero)
            return $"Your next line can come in {Math.Ceiling(wait.TotalSeconds):0} s.";
        var price = rules.LineCost > 0 && await points.ChargesAsync(guildId) ? rules.LineCost : 0;
        if (price > 0 && !await points.TrySpendAsync(guildId, member.Id, price, asks ? "asked through a helper" : "said through a helper"))
            return $"A line costs {points.Rules(guildId).Format(price)}, and you don't have that.";
        var line = $"{member.Nickname ?? member.GlobalName ?? member.Username} {(asks ? "asks" : "says")}: {text}";
        if (!await SpeakAsync(guildId, channelId, line, rules.Loudness))
        {
            if (price > 0)
                await points.RefundAsync(guildId, member.Id, price, "a line nobody could say");
            return "No helper can say it there right now.";
        }
        _cooldowns.Mark(guildId, member.Id, now);
        return $"🗣️ {line}{(price > 0 ? $" · {points.Rules(guildId).Format(price)}" : "")}";
    }

    private async Task<bool> SpeakAsync(ulong guildId, ulong channelId, string line, int loudness)
    {
        if (mouths.In(guildId, channelId) is not null || clips.Player?.PlayingIn(guildId, channelId) is not null)
        {
            await speech.SayAsync(guildId, channelId, line, SpeechKind.Line);
            return true;
        }
        try
        {
            return await hops.PlayAsync(guildId, channelId, async helper =>
                await voices.SayAsync(guildId, helper, line) is { } spoken ? (Pcm.Leveled(Pcm.ToDiscord(spoken.Sound), loudness), spoken.Sound.Length) : null, "a member's line");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException or RestException or WebSocketException)
        {
            logger.LogWarning("Saying a line in {ChannelId} of {GuildId} failed: {Message}", channelId, guildId, ex.Message);
            return false;
        }
    }
}

public sealed class LinePrices(SettingsStore settings, PointsEngine points, ModuleState modules) : IPriceSource
{
    public async Task<IReadOnlyList<Price>> PricesAsync(ulong guildId)
    {
        if (!await modules.IsEnabledAsync(guildId, HelperVoices.ModuleId))
            return [];
        var rules = await settings.GetAsync<SpeechRules>(guildId, HelperVoices.ModuleId);
        return !rules.Lines || rules.LineCost <= 0 ? [] : [new("A line said by a helper for you (/helper say, /helper ask)", $"{points.Rules(guildId).Format(rules.LineCost)}, one every {rules.LineCooldownSeconds} s")];
    }
}
