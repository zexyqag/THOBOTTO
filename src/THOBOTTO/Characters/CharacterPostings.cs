using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Characters;

public sealed class CharacterPostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
    {
        var channels = (await settings.GetAsync<CharacterRules>(guild.Id, CharacterBook.ModuleId)).ChannelIds;
        var setAt = Posting.Settings(guild.Id, CharacterBook.ModuleId);
        return channels.Count == 0
            ? [new(CharacterBook.ModuleId, "Messages under members' characters", new Landing.WhereStarted("wherever a member writes in a character's brackets"), setAt)]
            : [.. channels.Select(c => new Posting(CharacterBook.ModuleId, "Messages under members' characters", new Landing.In(c), setAt))];
    }
}
