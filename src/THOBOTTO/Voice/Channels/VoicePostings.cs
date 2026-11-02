using NetCord.Gateway;

using THOBOTTO.Modules;

namespace THOBOTTO.Voice.Channels;

public sealed class VoicePostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => [.. (await settings.GetAsync<VoiceRules>(guild.Id, DynamicVoice.ModuleId)).HubChannelIds
            .Select(hub => new Posting(DynamicVoice.ModuleId, "Voice channels of their own, made beside this hub", new Landing.In(hub), Posting.Settings(guild.Id, DynamicVoice.ModuleId)))];
}
