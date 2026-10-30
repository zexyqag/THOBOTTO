using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Wrapped;

namespace THOBOTTO.Commands;

public sealed partial class MeCommands
{
    [SubSlashCommand("wrapped", "Be named in this server's Wrapped stories and posts (leave out to see where you stand)")]
    public async Task<InteractionMessageProperties> WrappedAsync([SlashCommandParameter(Description = "On or off")] bool? named = null)
    {
        if (!await Get<ModuleState>().IsEnabledAsync(GuildId, WrappedPoster.ModuleId))
            return Replies.Ephemeral(ModuleRegistry.Off(WrappedPoster.ModuleId));
        var wrapped = Get<WrappedService>();
        if (named is { } wanted)
            await wrapped.SetInAsync(GuildId, Context.User.Id, wanted);
        return Replies.Ephemeral(await wrapped.IsInAsync(GuildId, Context.User.Id)
            ? "You're named in Wrapped here. The You page says more."
            : "You're only counted in Wrapped here, not named. `/me wrapped named:true` changes that.");
    }
}
