
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.GameServers;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("servers", "The game server board (needs servers.manage)")]
    [RequirePermission(BotPermissions.ManageServers)]
    public sealed class ServerSetup(ServerBoardService board, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Interaction.GuildId!.Value;

        [SubSlashCommand("board", "Choose the channel that shows server status")]
        public async Task<InteractionMessageProperties> BoardAsync(
            [SlashCommandParameter(Description = "Text channel", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel channel)
        {
            if ((await board.GetSettingsAsync(GuildId)).BoardChannelId == channel.Id)
                return Replies.Ephemeral($"Server status already goes to <#{channel.Id}>.");

            await board.UpdateSettingsAsync(GuildId, Context.User.Id, $"board={channel.Id}", s => s.BoardChannelId = channel.Id);

            var reply = $"Server status now goes to <#{channel.Id}>.";
            if (!await modules.IsEnabledAsync(GuildId, ServerBoardService.ModuleId))
                reply += $"\nThe `{ServerBoardService.ModuleId}` module is off; turn it on with `/setup modules enable`.";
            return Replies.Ephemeral(reply);
        }

        [SubSlashCommand("settings", "Who may add servers, and how often they're checked")]
        public async Task<InteractionMessageProperties> SettingsAsync(
            [SlashCommandParameter(Name = "members-can-add", Description = "Whether everyone can add servers, not just servers.manage")] bool? membersCanAdd = null,
            [SlashCommandParameter(Name = "per-member", Description = "Servers each member can add", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? perMember = null,
            [SlashCommandParameter(Name = "max-servers", Description = "Servers on the board in total", MinValue = 1, MaxValue = ServerSettings.MaxServersLimit)] int? maxServers = null,
            [SlashCommandParameter(Name = "poll-seconds", Description = "How often servers are checked", MinValue = ServerSettings.MinPollSeconds, MaxValue = ServerSettings.MaxPollSeconds)] int? pollSeconds = null,
            [SlashCommandParameter(Name = "offline-after", Description = "Failed checks in a row before a server shows as offline", MinValue = 1, MaxValue = ServerSettings.MaxFailuresBeforeOffline)] int? offlineAfter = null)
        {
            var before = await board.GetSettingsAsync(GuildId);
            var changes = new List<string>();
            if (membersCanAdd is { } m && m != before.MembersCanAdd) changes.Add($"members-can-add={m}");
            if (perMember is { } p && p != before.MaxPerMember) changes.Add($"per-member={p}");
            if (maxServers is { } x && x != before.MaxServers) changes.Add($"max-servers={x}");
            if (pollSeconds is { } s && s != before.PollSeconds) changes.Add($"poll-seconds={s}");
            if (offlineAfter is { } o && o != before.FailuresBeforeOffline) changes.Add($"offline-after={o}");
            var asked = membersCanAdd is not null || perMember is not null || maxServers is not null || pollSeconds is not null || offlineAfter is not null;

            if (changes.Count > 0)
            {
                await board.UpdateSettingsAsync(GuildId, Context.User.Id, string.Join(' ', changes), settings =>
                {
                    settings.MembersCanAdd = membersCanAdd ?? settings.MembersCanAdd;
                    settings.MaxPerMember = perMember ?? settings.MaxPerMember;
                    settings.MaxServers = maxServers ?? settings.MaxServers;
                    settings.PollSeconds = pollSeconds ?? settings.PollSeconds;
                    settings.FailuresBeforeOffline = offlineAfter ?? settings.FailuresBeforeOffline;
                });
            }

            var current = await board.GetSettingsAsync(GuildId);
            return Replies.Ephemeral($"""
                {(changes.Count > 0 ? "Updated. " : asked ? "Nothing changed. " : "")}Current settings:
                Board: {(current.BoardChannelId is { } c ? $"<#{c}>" : "not set (`/setup servers board`)")}
                Members can add servers: {(current.MembersCanAdd ? "yes" : "no, only `servers.manage`")}
                Per member: {current.MaxPerMember}
                Max servers: {current.MaxServers}
                Checked every {current.PollSeconds} s
                Offline after {current.FailuresBeforeOffline} failed check(s) in a row
                """);
        }
    }
}
