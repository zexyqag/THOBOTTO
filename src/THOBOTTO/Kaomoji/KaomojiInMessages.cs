using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Kaomoji;

// kao:shrug in a message becomes its face, the message posted again under the member's name.
public sealed class KaoWords(KaomojiShelf shelf, KaomojiFavourites favourites, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Words;

    public bool Wants(string text, ulong channelId, ulong userId) => KaomojiLibrary.HasWords(text);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (!preference.KaoWords || !await modules.IsEnabledAsync(guildId, KaomojiShelf.ModuleId) || !(await settings.GetAsync<KaomojiRules>(guildId, KaomojiShelf.ModuleId)).InMessages)
            return;
        var userId = repost.Member.Id;
        var (text, used) = KaomojiLibrary.Fill(repost.Text, await shelf.InAsync(guildId), await favourites.TextsAsync(userId));
        if (used.Count == 0)
            return;
        (repost.Text, repost.Changed) = (text, true);
        repost.Then.Add(async () =>
        {
            foreach (var face in used.DistinctBy(f => f.Text))
                await favourites.UsedAsync(userId, face.Text);
        });
    }
}
