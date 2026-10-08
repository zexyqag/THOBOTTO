using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Games;
using THOBOTTO.Modules;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("games", "Games, their modes, role pickers, session settings (needs games.manage)")]
    [RequirePermission(BotPermissions.ManageGames)]
    public sealed partial class GameSetup(GameDirectory games, ModuleState modules, SettingsStore settings, RestClient rest, TimeProvider time)
        : ApplicationCommandModule<ApplicationCommandContext>
    {
        private ulong GuildId => Context.Guild!.Id;

        private async Task<InteractionMessageProperties?> ModuleOffAsync()
            => await modules.IsEnabledAsync(GuildId, GameDirectory.ModuleId) ? null : Replies.Ephemeral($"The `{GameDirectory.ModuleId}` module is off.");

        [SubSlashCommand("add", "Add a game")]
        public async Task<InteractionMessageProperties> AddAsync(
            [SlashCommandParameter(Description = "Name", MaxLength = 80)] string name,
            [SlashCommandParameter(Description = "Its ping role (left out: I'll create one)")] Role? role = null,
            [SlashCommandParameter(Description = "An emoji for it, e.g. 🧟", MaxLength = 64)] string? emoji = null,
            [SlashCommandParameter(Description = "Its server on the server board", AutocompleteProviderType = typeof(GameServerAutocomplete))] string? server = null,
            [SlashCommandParameter(Description = "Where its sessions go by default")] Channel? channel = null,
            [SlashCommandParameter(Description = "How many can play together, e.g. 5; sessions fill up at that", MinValue = 1, MaxValue = 100)] int? players = null)
        {
            if (await ModuleOffAsync() is { } off)
                return off;
            if ((await games.ListAsync(GuildId)).Count >= GameDirectory.MaxGames)
                return Replies.Ephemeral($"There can be at most {GameDirectory.MaxGames} games (the picker's buttons run out).");
            if (emoji is not null && !IsEmoji(emoji))
                return Replies.Ephemeral("That emoji isn't one I can put on a button; use a standard one like 🧟.");

            var roleId = role?.Id ?? (await rest.CreateGuildRoleAsync(GuildId, new() { Name = name, Mentionable = true })).Id;
            var game = await games.AddAsync(new()
            {
                GuildId = GuildId,
                Name = name.Trim(),
                Emoji = emoji?.Trim(),
                RoleId = roleId,
                ServerId = long.TryParse(server, out var serverId) ? serverId : null,
                ChannelId = channel?.Id,
                Players = players,
                CreatedAt = time.GetUtcNow(),
            }, Context.User.Id);

            return Replies.Ephemeral($"Added {GameDirectory.Label(game)} with <@&{roleId}>.{(role is null ? " I created the role; it can be pinged." : "")} Members can pick it with `/game join` or a `/setup games picker` message.");
        }

        [SubSlashCommand("edit", "Change a game")]
        public async Task<InteractionMessageProperties> EditAsync(
            [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
            [SlashCommandParameter(Description = "New name", MaxLength = 80)] string? name = null,
            [SlashCommandParameter(Description = "An emoji for it; \"none\" to drop it", MaxLength = 64)] string? emoji = null,
            [SlashCommandParameter(Description = "Its server on the server board", AutocompleteProviderType = typeof(GameServerAutocomplete))] string? server = null,
            [SlashCommandParameter(Description = "Where its sessions go by default")] Channel? channel = null,
            [SlashCommandParameter(Description = "How many can play together; 0 for no limit", MinValue = 0, MaxValue = 100)] int? players = null)
        {
            if (await games.FindAsync(GuildId, game) is not { } found)
                return Replies.Ephemeral("There's no such game.");
            var dropEmoji = emoji?.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) == true;
            if (emoji is not null && !dropEmoji && !IsEmoji(emoji))
                return Replies.Ephemeral("That emoji isn't one I can put on a button; use a standard one like 🧟.");

            var changed = await games.UpdateAsync(found.Id, g =>
            {
                g.Name = name?.Trim() ?? g.Name;
                g.Emoji = dropEmoji ? null : emoji?.Trim() ?? g.Emoji;
                g.ServerId = long.TryParse(server, out var serverId) ? serverId : g.ServerId;
                g.ChannelId = channel?.Id ?? g.ChannelId;
                g.Players = players is null ? g.Players : players == 0 ? null : players;
            }, Context.User.Id);
            return Replies.Ephemeral($"Saved {GameDirectory.Label(changed)}{(changed.Players is { } p ? $": sessions fill up at {p}" : "")}.");
        }

        [SubSlashCommand("mode", "Add a mode with its player count, or change one's count")]
        public async Task<InteractionMessageProperties> ModeAsync(
            [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
            [SlashCommandParameter(Description = "Name, e.g. Wingman", MaxLength = 40)] string name,
            [SlashCommandParameter(Description = "How many play together in it", MinValue = 1, MaxValue = 100)] int players)
        {
            if (await games.FindAsync(Context.Guild!.Id, game) is not { } found)
                return Replies.Ephemeral("There's no such game.");
            var modes = await games.ModesAsync(found.Id);
            if (modes.Count >= 25 && !modes.Any(m => m.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Replies.Ephemeral("A game can have at most 25 modes.");
            var mode = await games.SetModeAsync(found, name.Trim(), players, Context.User.Id);
            return Replies.Ephemeral($"{GameDirectory.Label(found)} · **{mode.Name}**: {GameDirectory.PlayerCount(mode.Players)}. Pick it with `/session plan … mode:`.");
        }

        [SubSlashCommand("mode-remove", "Remove a mode (sessions already planned keep it)")]
        public async Task<InteractionMessageProperties> RemoveModeAsync(
            [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
            [SlashCommandParameter(Description = "Mode", AutocompleteProviderType = typeof(GameModeAutocomplete))] long mode)
        {
            if (await games.FindAsync(Context.Guild!.Id, game) is not { } found || (await games.ModesAsync(found.Id)).FirstOrDefault(m => m.Id == mode) is not { } existing)
                return Replies.Ephemeral("There's no such mode.");
            await games.RemoveModeAsync(found, existing, Context.User.Id);
            return Replies.Ephemeral($"Removed **{existing.Name}** from {GameDirectory.Label(found)}.");
        }

        [SubSlashCommand("remove", "Remove a game (its role stays; needs games.manage)")]
        public async Task<InteractionMessageProperties> RemoveAsync(
            [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
        {
            if (await games.FindAsync(GuildId, game) is not { } found)
                return Replies.Ephemeral("There's no such game.");
            await games.RemoveAsync(found, Context.User.Id);
            return Replies.Ephemeral($"Removed {GameDirectory.Label(found)}. Its role <@&{found.RoleId}> is still there; delete it in the server settings if you like.");
        }

        [SubSlashCommand("picker", "Post a message where members pick their games")]
        public async Task<InteractionMessageProperties> PickerAsync()
        {
            if (await ModuleOffAsync() is { } off)
                return off;
            await games.AddPickerAsync(GuildId, Context.Channel.Id);
            return Replies.Ephemeral("Posted. It updates itself when games change.");
        }

        [SubSlashCommand("settings", "Who may start sessions, and session offers")]
        public async Task<InteractionMessageProperties> SettingsAsync(
            [SlashCommandParameter(Name = "sessions-need-role", Description = "Only members with a game's role may start its sessions")] bool? sessionsNeedRole = null,
            [SlashCommandParameter(Name = "offer-sessions", Description = "Offer to make a session when someone pings a game role")] bool? offerSessions = null)
        {
            var before = await settings.GetAsync<GameRules>(GuildId, GameDirectory.ModuleId);
            var after = before with
            {
                SessionsNeedRole = sessionsNeedRole ?? before.SessionsNeedRole,
                OfferSessions = offerSessions ?? before.OfferSessions,
            };
            var changed = after != before;
            if (changed)
                await settings.SetAsync(GuildId, GameDirectory.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

            return Replies.Ephemeral($"""
                {(changed ? "Updated." : "Nothing changed.")}
                Sessions can be started by: {(after.SessionsNeedRole ? "members with the game's role" : "anyone")}
                Pinging a game role offers a session: {(after.OfferSessions ? "yes" : "no")}
                """);
        }

        // Buttons take standard emojis; a custom one would need its id, which we don't ask for.
        private static bool IsEmoji(string text) => !CustomEmoji().IsMatch(text) && text.Length <= 16 && !text.Any(char.IsLetterOrDigit);

        [GeneratedRegex(@"<a?:\w+:\d+>")]
        private static partial Regex CustomEmoji();
    }
}
