
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.GameServers;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("servers", "Game servers (needs " + BotPermissions.ManageGameServers + ")")]
    [RequirePermission(BotPermissions.ManageGameServers)]
    public sealed class ServerSetup(ServerWatch watch) : GuildCommandModule
    {
        [SubSlashCommand("settings", "Who may add game servers, and how often they're checked")]
        public async Task<InteractionMessageProperties> SettingsAsync(
            [SlashCommandParameter(Name = "members-can-add", Description = "Whether everyone can add game servers, not just " + BotPermissions.ManageGameServers)] bool? membersCanAdd = null,
            [SlashCommandParameter(Name = "per-member", Description = "Game servers each member can add", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? perMember = null,
            [SlashCommandParameter(Name = "max-servers", Description = "Game servers in total", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? maxServers = null,
            [SlashCommandParameter(Name = "poll-seconds", Description = "How often game servers are checked", MinValue = ServerSettings.MinPollSeconds, MaxValue = ServerSettings.MaxPollSeconds)] int? pollSeconds = null,
            [SlashCommandParameter(Name = "offline-after", Description = "Failed checks in a row before a game server shows as offline", MinValue = 1, MaxValue = ServerSettings.MaxFailuresBeforeOffline)] int? offlineAfter = null)
        {
            var before = await watch.GetSettingsAsync(GuildId);
            var changes = new List<string>();
            if (membersCanAdd is { } m && m != before.MembersCanAdd) changes.Add($"members-can-add={m}");
            if (perMember is { } p && p != before.MaxPerMember) changes.Add($"per-member={p}");
            if (maxServers is { } x && x != before.MaxServers) changes.Add($"max-servers={x}");
            if (pollSeconds is { } s && s != before.PollSeconds) changes.Add($"poll-seconds={s}");
            if (offlineAfter is { } o && o != before.FailuresBeforeOffline) changes.Add($"offline-after={o}");
            var asked = membersCanAdd is not null || perMember is not null || maxServers is not null || pollSeconds is not null || offlineAfter is not null;

            if (changes.Count > 0)
            {
                await watch.UpdateSettingsAsync(GuildId, Context.User.Id, string.Join(' ', changes), settings =>
                {
                    settings.MembersCanAdd = membersCanAdd ?? settings.MembersCanAdd;
                    settings.MaxPerMember = perMember ?? settings.MaxPerMember;
                    settings.MaxServers = maxServers ?? settings.MaxServers;
                    settings.PollSeconds = pollSeconds ?? settings.PollSeconds;
                    settings.FailuresBeforeOffline = offlineAfter ?? settings.FailuresBeforeOffline;
                });
            }

            var current = await watch.GetSettingsAsync(GuildId);
            return Replies.Ephemeral($"""
                {(changes.Count > 0 ? "Updated. " : asked ? "Nothing changed. " : "")}Current settings:
                Members can add game servers: {(current.MembersCanAdd ? "yes" : $"no, only `{BotPermissions.ManageGameServers}`")}
                Per member: {current.MaxPerMember}
                Max game servers: {current.MaxServers}
                Checked every {current.PollSeconds} s
                Offline after {current.FailuresBeforeOffline} failed check(s) in a row
                """);
        }
    }
}
