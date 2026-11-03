using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Kaomoji;

// Stored with SettingsStore under the module id.
public sealed record KaomojiRules
{
    [Setting("Post under the member's name", Help = "With their name and avatar, as if they wrote it (the bot needs Manage Webhooks). Off: as the bot's reply, which Discord heads with who used /kaomoji, linked to their account. In DMs it's always the bot's reply. Needs the Posting for members module; members can choose the bot's reply for theirs on their You page.")]
    public bool PostAsMember { get; init; } = true;

    [Setting("Fill in kao: words in messages", Help = "A message with kao:shrug or kao:table_flip in it is posted again under the member's name with the faces, and theirs removed (needs the Posting for members module). They edit it with right-click → Apps → Edit my post. Members can turn it off for theirs on their You page.")]
    public bool InMessages { get; init; } = true;
}

public sealed class KaomojiSummary(SettingsStore settings, ModuleState modules) : ISettingsSummary
{
    public string ModuleId => KaomojiShelf.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<KaomojiRules>(guildId, ModuleId);
        if (!await modules.IsEnabledAsync(guildId, Reposter.ModuleId))
            return $"Now: faces are posted as the bot's reply, and kao: words in messages are left as written: posting under members' names needs the {ModuleRegistry.Name(Reposter.ModuleId)} module.";
        var faces = rules.PostAsMember ? "faces are posted under the member's name" : "faces are posted as the bot's reply";
        var words = rules.InMessages ? "kao: words in messages are filled in" : "kao: words in messages are left as written";
        return $"Now: {faces}, and {words} (the {ModuleRegistry.Name(Reposter.ModuleId)} settings say how).";
    }
}
