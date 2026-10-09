
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
public sealed class GameCommands(GameDirectory games, ModuleState modules)
    : ApplicationCommandModule<ApplicationCommandContext>
{
    private ulong GuildId => Context.Guild!.Id;

    [SubSlashCommand("list", "The games")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var all = await games.ListAsync(GuildId);
        var lines = new List<string>();
        foreach (var g in all)
        {
            var modes = await games.ModesAsync(g.Id);
            lines.Add($"{GameDirectory.Label(g)}: <@&{g.RoleId}>{(g.Players is { } p ? $", {p} players" : "")}{(g.ChannelId is { } c ? $", sessions in <#{c}>" : "")}"
                + (modes.Count > 0 ? $"\n-# Modes: {string.Join(", ", modes.Select(m => $"{m.Name} ({m.Players})"))}" : ""));
        }
        return Replies.Ephemeral(all.Count == 0 ? "No games yet." : string.Join('\n', lines));
    }

    [SubSlashCommand("join", "Get a game's role, to hear about its sessions")]
    public Task<InteractionMessageProperties> JoinAsync([SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
        => SetRoleAsync(game, true);

    [SubSlashCommand("leave", "Drop a game's role")]
    public Task<InteractionMessageProperties> LeaveAsync([SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game)
        => SetRoleAsync(game, false);

    private async Task<InteractionMessageProperties> SetRoleAsync(long id, bool want)
    {
        if (await ModuleOffAsync() is { } off)
            return off;
        if (await games.FindAsync(GuildId, id) is not { } game)
            return Replies.Ephemeral("There's no such game.");
        await games.ToggleRoleAsync(GuildId, Context.User.Id, game, want);
        return Replies.Ephemeral(want
            ? $"You'll hear about {GameDirectory.Label(game)} now. For DMs about its sessions too: `/me notifications topic:{GameDirectory.Topic(game.Id)}`."
            : $"Dropped {GameDirectory.Label(game)}.");
    }

    private async Task<InteractionMessageProperties?> ModuleOffAsync()
        => await modules.IsEnabledAsync(GuildId, GameDirectory.ModuleId) ? null : Replies.Ephemeral($"The `{GameDirectory.ModuleId}` module is off.");

}

[SlashCommand("session", "Plan a game session", Contexts = [InteractionContextType.Guild])]
public sealed class SessionCommands(GameSessions sessions, TimeZones zones) : ApplicationCommandModule<ApplicationCommandContext>, IInteractionModule
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
        [SlashCommandParameter(Description = "A voice channel shortly before the start")] EventVoice voice = EventVoice.None,
        [SlashCommandParameter(Description = "Mode; sets how many can play", AutocompleteProviderType = typeof(GameModeAutocomplete))] long? mode = null,
        [SlashCommandParameter(Description = "How many can play together (default: the mode's or game's; 0 for no limit)", MinValue = 0, MaxValue = 100)] int? players = null)
        => await sessions.StartAsync(this, game, [when], title, voice, pingRole: true, modeId: mode, players: players);

    [SubSlashCommand("poll", "Vote on when to play; pings the game's role")]
    public async Task PollAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
        [SlashCommandParameter(Description = "Candidate times, comma-separated: fri 20:00, sat 18:00", MaxLength = 400)] string times,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null,
        [SlashCommandParameter(Description = "A voice channel shortly before the start")] EventVoice voice = EventVoice.None,
        [SlashCommandParameter(Description = "Mode; sets how many can play", AutocompleteProviderType = typeof(GameModeAutocomplete))] long? mode = null,
        [SlashCommandParameter(Description = "How many can play together (default: the mode's or game's; 0 for no limit)", MinValue = 0, MaxValue = 100)] int? players = null)
        => await sessions.StartAsync(this, game, times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), title, voice, pingRole: true, poll: true, modeId: mode, players: players);

    [SubSlashCommand("recurring", "A session every week on set days; each one opens ahead and pings the game's role")]
    public async Task RecurringAsync(
        [SlashCommandParameter(Description = "Game", AutocompleteProviderType = typeof(GameAutocomplete))] long game,
        [SlashCommandParameter(Description = "fri, or mon, thu, or daily, weekdays, weekends", MaxLength = 60)] string days,
        [SlashCommandParameter(Name = "time", Description = "In your time, e.g. 20:00 or 8pm", MaxLength = 10)] string clockText,
        [SlashCommandParameter(Description = "Mode; sets how many can play", AutocompleteProviderType = typeof(GameModeAutocomplete))] long? mode = null,
        [SlashCommandParameter(Description = "How many can play together (default: the mode's or game's; 0 for no limit)", MinValue = 0, MaxValue = 100)] int? players = null,
        [SlashCommandParameter(Description = "Title (default: \"<game> session\")", MaxLength = 100)] string? title = null,
        [SlashCommandParameter(Description = "A voice channel shortly before each start")] EventVoice voice = EventVoice.None,
        [SlashCommandParameter(Name = "open-days-ahead", Description = "How early each one opens (default 3 days)", MinValue = 1, MaxValue = 30)] int openDaysAhead = 3)
    {
        if (Recurrence.ParseDays(days) is not { } dayList)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Days are like `fri`, `mon, thu`, `daily`, `weekdays` or `weekends`.")));
            return;
        }
        if (WhenParser.ParseClock(clockText) is not { } clock)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("The time is like `20:00` or `8pm`.")));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (zone, _) = await zones.ForAsync(Context.Guild!.Id, Context.User.Id);
        var (series, problem) = await sessions.CreateSeriesAsync(Context.Guild, (GuildUser)Context.User, game, dayList, clock, zone, title, voice, pingRole: true, mode, players, Context.Channel.Id, openDaysAhead);
        await ModifyResponseAsync(m => m.Content = series is null
            ? problem
            : $"**{series.Title}** {Recurrence.Describe(dayList)} at {clock:HH:mm} {zone.Id} time; each one opens {openDaysAhead} days ahead in <#{series.ChannelId}>.");
    }

    [SubSlashCommand("edit", "Change a session's mode or player count; more room moves those waiting up")]
    public async Task<InteractionMessageProperties> EditAsync(
        [SlashCommandParameter(Description = "Session", AutocompleteProviderType = typeof(EventAutocomplete))] long session,
        [SlashCommandParameter(Description = "Mode; sets how many can play", AutocompleteProviderType = typeof(GameModeAutocomplete))] long? mode = null,
        [SlashCommandParameter(Description = "How many can play together; 0 for no limit", MinValue = 0, MaxValue = 100)] int? players = null)
        => Replies.Ephemeral(await sessions.EditAsync(Context.Guild!, Context.User, session, mode, players));
}

// Starting sessions, shared by /session and the "make it a session" offer.
public sealed class GameSessions(
    GameDirectory games,
    EventBoard board,
    TimeZones zones,
    ModuleState modules,
    SettingsStore settings,
    AccessControl access,
    TimeProvider time)
{
    public async Task StartAsync(IInteractionModule module, long gameId, IReadOnlyList<string> times, string? title, EventVoice voice, bool pingRole,
        bool poll = false, long? modeId = null, int? players = null)
    {
        var guild = module.Guild;
        var (zone, own) = await zones.ForAsync(guild.Id, module.User.Id);
        var now = Instant.FromDateTimeOffset(time.GetUtcNow());
        var parsed = times.Select(t => (Text: t, When: WhenParser.Parse(t, zone, now))).ToList();
        if (parsed.FirstOrDefault(p => p.When.At is null) is { Text: not null } bad)
        {
            await module.RespondAsync(InteractionCallback.Message(Replies.Ephemeral($"`{bad.Text}`: {bad.When.Problem}\n{EventCommands.ZoneHint(zone, own)}")));
            return;
        }

        await module.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (e, problem) = await CreateAsync(guild, (GuildUser)module.User, gameId, parsed.Select(p => p.When.At!.Value.ToDateTimeOffset()).ToList(),
            title, voice, pingRole, poll, modeId, players, module.ChannelId);
        await module.ModifyResponseAsync(m => m.Content = e is null
            ? problem
            : $"Session {e.Id} is up{(e.ChannelId != module.ChannelId ? $" in <#{e.ChannelId}>" : "")}. {EventCommands.ZoneHint(zone, own)}");
    }

    // Plans a session (several times make a poll) in the game's channel, else the given one; or says why not.
    public async Task<(Event? Event, string? Problem)> CreateAsync(Guild guild, GuildUser user, long gameId, IReadOnlyList<DateTimeOffset> startTimes,
        string? title, EventVoice voice, bool pingRole, bool poll, long? modeId, int? players, ulong channelId)
    {
        var refusal = await RefusalAsync(guild, user, gameId);
        var mode = modeId is { } id ? (await games.ModesAsync(gameId)).FirstOrDefault(m => m.Id == id) : null;
        refusal ??= modeId is not null && mode is null ? "That game has no such mode; `/game list` shows its modes."
            : startTimes.Count == 0 ? "Give a time."
            : startTimes.Count > EventBoard.MaxTimeOptions ? $"At most {EventBoard.MaxTimeOptions} times."
            : startTimes.Any(t => t <= time.GetUtcNow()) ? "That time has already passed."
            : null;
        if (refusal is not null)
            return (null, refusal);

        var game = (await games.FindAsync(guild.Id, gameId))!;
        channelId = game.ChannelId ?? channelId;
        var name = string.IsNullOrWhiteSpace(title) ? DefaultTitle(game, mode?.Name) : title.Trim();
        var voiceMode = voice switch { EventVoice.Open => VoiceModes.Open, EventVoice.Locked => VoiceModes.Locked, _ => null };
        var discord = (await settings.GetAsync<EventRules>(guild.Id, EventBoard.ModuleId)).DiscordEvents;
        var capacity = players switch { null => mode?.Players ?? game.Players, 0 => null, _ => players };

        var e = poll || startTimes.Count > 1
            ? await board.CreatePollAsync(guild.Id, channelId, user.Id, name, null, pingRole ? game.RoleId : null, startTimes, time.GetUtcNow() + TimeSpan.FromHours(24), true, voiceMode, discord, game.Id, capacity, mode?.Name)
            : await board.CreateAsync(guild.Id, channelId, user.Id, name, null, pingRole ? game.RoleId : null, startTimes[0], voiceMode, discord, game.Id, capacity, mode?.Name);

        return (e, null);
    }

    // A session that repeats on these weekdays at this time of day in the zone; each one opens some days ahead.
    public async Task<(EventSeries? Series, string? Problem)> CreateSeriesAsync(Guild guild, GuildUser user, long gameId, int[] days, LocalTime timeOfDay,
        DateTimeZone zone, string? title, EventVoice voice, bool pingRole, long? modeId, int? players, ulong channelId, int openDaysAhead)
    {
        var refusal = await RefusalAsync(guild, user, gameId);
        var mode = modeId is { } id ? (await games.ModesAsync(gameId)).FirstOrDefault(m => m.Id == id) : null;
        refusal ??= modeId is not null && mode is null ? "That game has no such mode; `/game list` shows its modes."
            : days.Length == 0 ? "Pick at least one weekday."
            : null;
        if (refusal is not null)
            return (null, refusal);

        var game = (await games.FindAsync(guild.Id, gameId))!;
        var series = await board.CreateSeriesAsync(new()
        {
            GuildId = guild.Id,
            ChannelId = game.ChannelId ?? channelId,
            CreatorId = user.Id,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(game, mode?.Name) : title.Trim(),
            PingRoleId = pingRole ? game.RoleId : null,
            Days = days,
            TimeOfDay = timeOfDay.Hour * 60 + timeOfDay.Minute,
            Zone = zone.Id,
            OpenDaysAhead = openDaysAhead,
            VoiceMode = voice switch { EventVoice.Open => VoiceModes.Open, EventVoice.Locked => VoiceModes.Locked, _ => null },
            WantsDiscordEvent = (await settings.GetAsync<EventRules>(guild.Id, EventBoard.ModuleId)).DiscordEvents,
            Capacity = players switch { null => mode?.Players ?? game.Players, 0 => null, _ => players },
            GameId = game.Id,
            Mode = mode?.Name,
            CreatedAt = time.GetUtcNow(),
        });
        return (series, null);
    }

    public async Task<string> EditAsync(Guild guild, User user, long sessionId, long? modeId, int? players)
    {
        var e = await board.FindAsync(guild.Id, sessionId);
        if (e is not { GameId: { } gameId } || e.State != EventStates.Scheduled)
            return "There's no such upcoming session.";
        if (e.CreatorId != user.Id && !await access.CanAsync(guild, (GuildUser)user, BotPermissions.ManageEvents))
            return $"Only <@{e.CreatorId}> or someone with `{BotPermissions.ManageEvents}` can change it.";
        if (modeId is null && players is null)
            return "Give a mode or a player count.";

        var mode = modeId is { } id ? (await games.ModesAsync(gameId)).FirstOrDefault(m => m.Id == id) : null;
        if (modeId is not null && mode is null)
            return "That game has no such mode; `/game list` shows its modes.";

        var capacity = players switch { null => mode!.Players, 0 => null, _ => players };
        var game = await games.FindAsync(guild.Id, gameId);
        var title = e.Title;
        // A title that was just the default names the old mode; a chosen one stays.
        if (mode is not null && game is not null && e.FirstPartId is null && e.Title == DefaultTitle(game, e.Mode))
            title = DefaultTitle(game, mode.Name);
        var moved = await board.ChangeLimitAsync(e.Id, x =>
        {
            x.Capacity = capacity;
            x.Mode = mode?.Name ?? x.Mode;
            x.Title = title;
        });
        return EventCommands.LimitText(title, capacity, moved) + (mode is null ? "" : $" Mode: {mode.Name}.");
    }

    private static string DefaultTitle(Game game, string? mode) => $"{game.Name}{(mode is null ? "" : $" {mode}")} session";

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
                ? $"You'll hear about {GameDirectory.Label(game)} now. For DMs about its sessions: `/me notifications topic:{GameDirectory.Topic(game.Id)}`."
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

// The modes of the game picked in the same command, or of the session's game.
public sealed class GameModeAutocomplete(GameDirectory games, EventBoard board) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var guildId = context.Interaction.GuildId!.Value;
        var options = Flatten(context.Interaction.Data.Options).ToList();
        long? gameId = long.TryParse(options.FirstOrDefault(o => o.Name == "game")?.Value, out var g) ? g
            : long.TryParse(options.FirstOrDefault(o => o.Name == "session")?.Value, out var s) ? (await board.FindAsync(guildId, s))?.GameId
            : null;
        if (gameId is null || await games.FindAsync(guildId, gameId.Value) is null)
            return [];

        var input = option.Value ?? "";
        return (await games.ModesAsync(gameId.Value))
            .Where(m => m.Name.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(m => new ApplicationCommandOptionChoiceProperties($"{m.Name} ({GameDirectory.PlayerCount(m.Players)})", m.Id));
    }

    private static IEnumerable<ApplicationCommandInteractionDataOption> Flatten(IEnumerable<ApplicationCommandInteractionDataOption>? options)
        => options?.SelectMany(o => Flatten(o.Options).Prepend(o)) ?? [];
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
