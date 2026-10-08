
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("music", "Music: idle time, queue size, search source, DJ rule (needs Get<MusicService>().manage)")]
    [RequirePermission(BotPermissions.ManageMusic)]
    public async Task<InteractionMessageProperties> MusicAsync(
        [SlashCommandParameter(Name = "idle-minutes", Description = "Leave after this long with nobody listening or nothing playing", MinValue = 1, MaxValue = 120)] int? idleMinutes = null,
        [SlashCommandParameter(Name = "max-queue", Description = "Most tracks in a queue", MinValue = 1, MaxValue = 5000)] int? maxQueue = null,
        [SlashCommandParameter(Name = "search", Description = "Where plain words are searched")] SearchSource? search = null,
        [SlashCommandParameter(Name = "dj-only", Description = "Skip, stop, pause and volume need Get<MusicService>().dj (your own track you can always skip)")] bool? djOnly = null)
    {
        var guildId = Context.Guild!.Id;
        var before = await Get<SettingsStore>().GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        var after = before with
        {
            IdleMinutes = idleMinutes ?? before.IdleMinutes,
            MaxQueue = maxQueue ?? before.MaxQueue,
            DefaultSearch = search switch { SearchSource.YouTube => "ytsearch", SearchSource.SoundCloud => "scsearch", _ => before.DefaultSearch },
            DjOnly = djOnly ?? before.DjOnly,
        };
        var changed = after != before;
        if (changed)
            await Get<SettingsStore>().SetAsync(guildId, MusicService.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Leave after {after.IdleMinutes} min idle · queue up to {after.MaxQueue} · search {(after.DefaultSearch == "scsearch" ? "SoundCloud" : "YouTube")} · controls: {(after.DjOnly ? $"`{BotPermissions.MusicDj}` only" : "anyone")}
            """);
    }
}
