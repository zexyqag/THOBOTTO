using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using NodaTime;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Events;
using THOBOTTO.Modules;
using THOBOTTO.Notifications;

namespace THOBOTTO.Games;

[SlashCommand("game", "Games, their roles and sessions", Contexts = [InteractionContextType.Guild])]
public sealed partial class GameCommands(GameDirectory games, ModuleState modules, SettingsStore settings, RestClient rest, TimeProvider time)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("add", "Add a game (needs games.manage)")]
    [RequirePermission(BotPermissions.ManageGames)]
    public async Task<InteractionMessageProperties> AddAsync(
        [SlashCommandParameter(Description = "Name", MaxLength = 80)] string name,
        [SlashCommandParameter(Description = "Its ping role (left out: I'll create one)")] Role? role = null,
        [SlashCommandParameter(Description = "An emoji for it, e.g. 🧟", MaxLength = 64)] string? emoji = null,
        [SlashCommandParameter(Description = "Its server on the server board", AutocompleteProviderType = typeof(GameServerAutocomplete))] string? server = null,
        [SlashCommandParameter(Description = "Where its sessions go by default")] Channel? channel = null)
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
            CreatedAt = time.GetUtcNow(),
        }, Context.User.Id);

        return Replies.Ephemeral($"Added {GameDirectory.Label(game)} with <@&{roleId}>.{(role is null ? " I created the role; it can be pinged." : "")} Members can pick it with `/game join` or a `/game picker` message.");
    }

    [SubSlashCommand("remove", "Remove a game (its role stays; needs games.manage)")]
    [RequirePermission(BotPermissions.ManageGames)]
    public async Task<InteractionMessageProperties> RemoveAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
    {
        if (await games.FindAsync(GuildId, game) is not { } found)
            return Replies.Ephemeral("There's no such game.");
        await games.RemoveAsync(found, Context.User.Id);
        return Replies.Ephemeral($"Removed {GameDirectory.Label(found)}. Its role <@&{found.RoleId}> is still there; delete it in the server settings if you like.");
    }

    [SubSlashCommand("list", "The games")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var all = await games.ListAsync(GuildId);
        return Replies.Ephemeral(all.Count == 0
            ? "No games yet."
            : string.Join('\n', all.Select(g => $"{GameDirectory.Label(g)}: <@&{g.RoleId}>{(g.ChannelId is { } c ? $", sessions in <#{c}>" : "")}")));
    }

    [SubSlashCommand("join", "Get a game's role, to hear about its sessions")]
    public Task<InteractionMessageProperties> JoinAsync([SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
        => SetRoleAsync(game, true);

    [SubSlashCommand("leave", "Drop a game's role")]
    public Task<InteractionMessageProperties> LeaveAsync([SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
        => SetRoleAsync(game, false);

    [SubSlashCommand("picker", "Post a message where members pick their games (needs games.manage)")]
    [RequirePermission(BotPermissions.ManageGames)]
    public async Task<InteractionMessageProperties> PickerAsync()
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        await games.AddPickerAsync(GuildId, Context.Channel.Id);
        return Replies.Ephemeral("Posted. It updates itself when games change.");
    }

    [SubSlashCommand("settings", "Who may start sessions, and session offers (needs games.manage)")]
    [RequirePermission(BotPermissions.ManageGames)]
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

    private async Task<InteractionMessageProperties> SetRoleAsync(long id, bool want)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if (await games.FindAsync(GuildId, id) is not { } game)
            return Replies.Ephemeral("There's no such game.");
        await games.ToggleRoleAsync(GuildId, Context.User.Id, game, want);
        return Replies.Ephemeral(want
            ? $"You'll hear about {GameDirectory.Label(game)} now. For DMs about its sessions too: `/notify set topic:{GameDirectory.Topic(game.Id)}`."
            : $"Dropped {GameDirectory.Label(game)}.");
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, GameDirectory.ModuleId) ? null : Replies.Ephemeral($"The `{GameDirectory.ModuleId}` module is off.");

    // Buttons take standard emojis; a custom one would need its id, which we don't ask for.
    private static bool IsEmoji(string text) => !CustomEmoji().IsMatch(text) && text.Length <= 16 && !text.Any(char.IsLetterOrDigit);

    [GeneratedRegex(@"<a?:\w+:\d+>")]
    private static partial Regex CustomEmoji();
}

[SlashCommand("session", "Plan a game session", Contexts = [InteractionContextType.Guild])]
public sealed class SessionCommands(GameSessions sessions) : ApplicationCommandModule<ApplicationCommandContext>, IInteractionModule
{
    Guild IInteractionModule.Guild => Context.Guild!;
    User IInteractionModule.User => Context.User;
    ulong IInteractionModule.ChannelId => Context.Channel.Id;
    Task IInteractionModule.RespondAsync(InteractionCallbackProperties callback) => RespondAsync(callback);
    Task IInteractionModule.ModifyResponseAsync(Action<MessageOptions> modify) => ModifyResponseAsync(modify);

    [SubSlashCommand("plan", "A session at a set time; pings the game's role")]
    public async Task PlanAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
        [SlashCommandParameter(Description = "When, in your time: 20:00, fri 8pm, tomorrow 19:30", MaxLength = 50)] string when,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null,
        [SlashCommandParameter(Description = "A voice channel shortly before the start")] EventVoice voice = EventVoice.None)
        => await sessions.StartAsync(this, game, [when], title, voice, pingRole: true);

    [SubSlashCommand("poll", "Vote on when to play; pings the game's role")]
    public async Task PollAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
        [SlashCommandParameter(Description = "Candidate times, comma-separated: fri 20:00, sat 18:00", MaxLength = 400)] string times,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null,
        [SlashCommandParameter(Description = "A voice channel shortly before the start")] EventVoice voice = EventVoice.None)
        => await sessions.StartAsync(this, game, times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), title, voice, pingRole: true, poll: true);
}

// Starting sessions, shared by /session and the "make it a session" offer.
public sealed class GameSessions(
    GameDirectory games,
    EventBoard board,
    TimeZones zones,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    Notifier notifier,
    TimeProvider time)
{
    public async Task StartAsync(IInteractionModule module, long gameId, IReadOnlyList<string> times, string? title, EventVoice voice, bool pingRole, bool poll = false)
    {
        var guild = module.Guild;
        var user = module.User;
        var (zone, own) = await zones.ForAsync(guild.Id, user.Id);

        var refusal = await RefusalAsync(guild, user, gameId);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow());
        var parsed = times.Select(t => (Text: t, When: WhenParser.Parse(t, zone, now))).ToList();
        refusal ??= parsed.Count == 0 ? "Give a time."
            : parsed.Count > EventBoard.MaxTimeOptions ? $"At most {EventBoard.MaxTimeOptions} times."
            : parsed.FirstOrDefault(p => p.When.At is null) is { Text: not null } bad ? $"`{bad.Text}`: {bad.When.Problem}\n{EventCommands.ZoneHint(zone, own)}"
            : null;
        if (refusal is not null)
        {
            await module.RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refusal)));
            return;
        }

        await module.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var game = (await games.FindAsync(guild.Id, gameId))!;
        var channelId = game.ChannelId ?? module.ChannelId;
        var name = string.IsNullOrWhiteSpace(title) ? $"{game.Name} session" : title.Trim();
        var voiceMode = voice switch { EventVoice.Open => VoiceModes.Open, EventVoice.Locked => VoiceModes.Locked, _ => null };
        var discord = (await settings.GetAsync<EventRules>(guild.Id, EventBoard.ModuleId)).DiscordEvents;
        var startTimes = parsed.Select(p => p.When.At!.Value.ToDateTimeOffset()).ToList();

        var e = poll || startTimes.Count > 1
            ? await board.CreatePollAsync(guild.Id, channelId, user.Id, name, null, pingRole ? game.RoleId : null, startTimes, time.GetUtcNow() + TimeSpan.FromHours(24), true, voiceMode, discord, game.Id)
            : await board.CreateAsync(guild.Id, channelId, user.Id, name, null, pingRole ? game.RoleId : null, startTimes[0], voiceMode, discord, game.Id);

        await notifier.NotifySubscribersAsync(guild.Id, GameDirectory.Topic(game.Id), $"new {game.Name} session: **{name}**",
            e.MessageId is { } m ? Notifier.Link(guild.Id, channelId, m) : null);
        await module.ModifyResponseAsync(m => m.Content = $"Session {e.Id} is up{(channelId != module.ChannelId ? $" in <#{channelId}>" : "")}. {EventCommands.ZoneHint(zone, own)}");
    }

    private async Task<string?> RefusalAsync(Guild guild, User user, long gameId)
    {
        if (!await modules.IsEnabledAsync(guild.Id, GameDirectory.ModuleId) || !await modules.IsEnabledAsync(guild.Id, EventBoard.ModuleId))
            return $"Sessions need the `{GameDirectory.ModuleId}` and `{EventBoard.ModuleId}` modules.";
        if (await games.FindAsync(guild.Id, gameId) is not { } game)
            return "There's no such game.";

        var member = (GuildUser)user;
        var eventRules = await settings.GetAsync<EventRules>(guild.Id, EventBoard.ModuleId);
        if (eventRules.CreateNeedsPermission && !await access.CanAsync(guild, member, BotPermissions.CreateEvents))
            return $"Planning events needs `{BotPermissions.CreateEvents}` here.";
        var rules = await settings.GetAsync<GameRules>(guild.Id, GameDirectory.ModuleId);
        if (rules.SessionsNeedRole && !member.RoleIds.Contains(game.RoleId))
            return $"Only members with <@&{game.RoleId}> may start its sessions.";
        return null;
    }
}

// What GameSessions needs from a slash command or a modal, which come from different module types.
public interface IInteractionModule
{
    Guild Guild { get; }
    User User { get; }
    ulong ChannelId { get; }
    Task RespondAsync(InteractionCallbackProperties callback);
    Task ModifyResponseAsync(Action<MessageOptions> modify);
}

public sealed class GameRoleButtons(GameDirectory games) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("gamerole")]
    public async Task ToggleAsync(long gameId)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var game = await games.FindAsync(Context.Guild!.Id, gameId);
        var result = game is null
            ? "That game was removed."
            : await games.ToggleRoleAsync(Context.Guild!.Id, Context.User.Id, game)
                ? $"You'll hear about {GameDirectory.Label(game)} now. For DMs about its sessions: `/notify set topic:{GameDirectory.Topic(game.Id)}`."
                : $"Dropped {GameDirectory.Label(game)}.";
        await ModifyResponseAsync(m => m.Content = result);
    }

    // The "make it a session" offer: only whoever pinged may use it.
    [ComponentInteraction("gameoffer")]
    public InteractionCallbackProperties Offer(long gameId, ulong authorId)
        => Context.User.Id != authorId
            ? InteractionCallback.Message(Replies.Ephemeral("Only whoever pinged can turn this into a session."))
            : InteractionCallback.Modal(new ModalProperties($"gameoffernew:{gameId}", "Make it a session")
            {
                new LabelProperties("When, in your time?", new TextInputProperties("when", TextInputStyle.Short)
                {
                    Placeholder = "fri 20:00, or several for a vote: fri 20:00, sat 18:00",
                    MaxLength = 200,
                }),
                new LabelProperties("Title (optional)", new TextInputProperties("title", TextInputStyle.Short) { Required = false, MaxLength = 100 }),
            });
}

public sealed class GameOfferModal(GameSessions sessions) : ComponentInteractionModule<ModalInteractionContext>, IInteractionModule
{
    Guild IInteractionModule.Guild => Context.Guild!;
    User IInteractionModule.User => Context.User;
    ulong IInteractionModule.ChannelId => Context.Channel.Id;
    Task IInteractionModule.RespondAsync(InteractionCallbackProperties callback) => RespondAsync(callback);
    Task IInteractionModule.ModifyResponseAsync(Action<MessageOptions> modify) => ModifyResponseAsync(modify);

    [ComponentInteraction("gameoffernew")]
    public async Task CreateAsync(long gameId)
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value);
        var times = inputs["when"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The role was just pinged by the member, so the session itself doesn't ping it again.
        await sessions.StartAsync(this, gameId, times, inputs.GetValueOrDefault("title"), EventVoice.None, pingRole: false);
    }
}

// When someone pings a game role in a message, offer to turn it into a session.
public sealed class GamePingHandler(GameDirectory games, ModuleState modules, SettingsStore settings, RestClient rest, ILogger<GamePingHandler> logger)
    : IMessageCreateGatewayHandler
{
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(15);

    public async ValueTask HandleAsync(Message message)
    {
        if (message.GuildId is not { } guildId || message.Author.IsBot || message.MentionedRoleIds.Count == 0)
            return;
        if (!await modules.IsEnabledAsync(guildId, GameDirectory.ModuleId) || !(await settings.GetAsync<GameRules>(guildId, GameDirectory.ModuleId)).OfferSessions)
            return;

        foreach (var roleId in message.MentionedRoleIds)
        {
            if (await games.ByRoleAsync(guildId, roleId) is not { } game)
                continue;

            var offer = await rest.SendMessageAsync(message.ChannelId, new()
            {
                Content = $"Planning some {game.Name}?",
                Components = [new ActionRowProperties { new ButtonProperties($"gameoffer:{game.Id}:{message.Author.Id}", "Make it a session", EmojiProperties.Standard("📅"), ButtonStyle.Primary) }],
                MessageReference = MessageReferenceProperties.Reply(message.Id, failIfNotExists: false),
                AllowedMentions = AllowedMentionsProperties.None,
            });
            _ = RemoveLaterAsync(message.ChannelId, offer.Id);
            return;
        }
    }

    // Offers are clutter once stale; a restart in between just leaves one behind.
    private async Task RemoveLaterAsync(ulong channelId, ulong messageId)
    {
        await Task.Delay(OfferLifetime);
        try
        {
            await rest.DeleteMessageAsync(channelId, messageId);
        }
        catch (RestException ex)
        {
            logger.LogDebug("Offer {MessageId} already gone: {Message}", messageId, ex.Message);
        }
    }
}

public sealed class GameAutocomplete(GameDirectory games) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        var all = await games.ListAsync(context.Interaction.GuildId!.Value);
        return all.Where(g => g.Name.Contains(input, StringComparison.OrdinalIgnoreCase)).Take(25)
            .Select(g => new ApplicationCommandOptionChoiceProperties(GameDirectory.Label(g), g.Id));
    }
}

public sealed class GameServerAutocomplete(IDbContextFactory<BotDbContext> dbFactory) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var guildId = context.Interaction.GuildId!.Value;
        await using var db = await dbFactory.CreateDbContextAsync();
        var servers = await db.GameServers.Where(s => s.GuildId == guildId).ToListAsync();
        return servers.Take(25).Select(s => (s.Id, Label: s.Name is null ? $"{s.Game} {s.Address}" : $"{s.Name} ({s.Address})"))
            .Select(s => new ApplicationCommandOptionChoiceProperties(s.Label.Length <= 100 ? s.Label : s.Label[..100], s.Id.ToString()));
    }
}
