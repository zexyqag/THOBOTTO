using System.Collections.Concurrent;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Games;

// The server's games as a question in Discord's onboarding (a Community server's, as set): new members pick their games
// while joining, each answer giving the game's role, which is all the bot reads back. Written when games or the settings
// change; Discord takes the whole list of questions at once, so the server's others are sent back as they are.
public sealed class GameOnboarding(GameDirectory games, SettingsStore settings, ModuleState modules, GatewayClient gateway, RestClient rest, TimeProvider time, ILogger<GameOnboarding> logger)
    : IHostedService
{
    private const int TitleLength = 50;

    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _gates = new();
    // Whether the question was wanted when last looked at (unknown after a start: looked at once). Discord limits how
    // often onboarding is read and written, so it's left alone while the setting stays off.
    private readonly ConcurrentDictionary<ulong, bool> _wanted = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        games.Changed += SyncAsync;
        settings.Changed += (guildId, module) => module == GameDirectory.ModuleId ? SyncAsync(guildId) : Task.CompletedTask;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task SyncAsync(ulong guildId)
    {
        if (!gateway.IsCommunity(guildId))
            return;
        var gate = _gates.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var rules = await settings.GetAsync<GameRules>(guildId, GameDirectory.ModuleId);
            var listed = await games.ListAsync(guildId);
            var wanted = rules.InOnboarding && await modules.IsEnabledAsync(guildId, GameDirectory.ModuleId) && listed.Count > 0;
            if (!wanted && _wanted.TryGetValue(guildId, out var was) && !was)
                return;
            var onboarding = await rest.GetGuildOnboardingAsync(guildId);
            var roles = listed.Select(g => g.RoleId).ToHashSet();
            var ours = Ours(onboarding.Prompts, rules.OnboardingQuestion, roles);
            if (!wanted && ours is null)
            {
                _wanted[guildId] = false;
                return;
            }
            var prompts = onboarding.Prompts.Select(p => p == ours ? null : Copy(p)).ToList();
            if (wanted)
            {
                var made = Question(rules.OnboardingQuestion, listed, ours, Fresh());
                if (ours is not null && Same(ours, made))
                {
                    _wanted[guildId] = true;
                    return;
                }
                var at = ours is null ? prompts.Count : prompts.IndexOf(null);
                if (ours is null)
                    prompts.Add(made);
                else
                    prompts[at] = made;
            }
            await rest.ModifyGuildOnboardingAsync(guildId, o => o.Prompts = prompts.OfType<GuildOnboardingPromptProperties>());
            _wanted[guildId] = wanted;
        }
        catch (RestException ex)
        {
            logger.LogWarning("Keeping the games' question in Discord's onboarding of {GuildId} failed: {Message}", guildId, ex.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    // The bot's question: by its title, else the one whose answers are all games (its title changed here since).
    public static GuildOnboardingPrompt? Ours(IReadOnlyList<GuildOnboardingPrompt> prompts, string title, IReadOnlySet<ulong> gameRoles)
        => prompts.FirstOrDefault(p => p.Title == Clip(title))
            ?? prompts.FirstOrDefault(p => p.Options.Count > 0 && p.Options.All(o => o.ChannelIds.Count == 0 && o.RoleIds.Count == 1 && gameRoles.Contains(o.RoleIds[0])));

    // Discord wants an id on a new question and answer (one of its own takes its place): ids as of now, one after another.
    private Func<ulong> Fresh()
    {
        var next = (ulong)(time.GetUtcNow().ToUnixTimeMilliseconds() - 1420070400000) << 22;
        return () => next++;
    }

    // A game's answer keeps its id (and so who picked it before): the one with its title, else one with its role, each once.
    public static GuildOnboardingPromptProperties Question(string title, IReadOnlyList<Game> listed, GuildOnboardingPrompt? before, Func<ulong> fresh)
    {
        var free = before?.Options.ToList() ?? [];
        var options = listed.Select(g =>
        {
            var kept = free.FirstOrDefault(o => o.Title == Clip(g.Name) && o.RoleIds.Contains(g.RoleId)) ?? free.FirstOrDefault(o => o.RoleIds.Contains(g.RoleId));
            if (kept is not null)
                free.Remove(kept);
            return new GuildOnboardingPromptOptionProperties([], [g.RoleId], Clip(g.Name)) { Id = kept?.Id ?? fresh(), EmojiName = GameDirectory.StandardEmoji(g.Emoji) };
        }).ToList();
        return new(GuildOnboardingPromptType.MultipleChoice, options, Clip(title))
        {
            Id = before?.Id ?? fresh(),
            SingleSelect = false,
            Required = false,
            InOnboarding = true,
        };
    }

    private static bool Same(GuildOnboardingPrompt now, GuildOnboardingPromptProperties made)
    {
        var options = made.Options.ToList();
        return now.Title == made.Title && now.InOnboarding && !now.SingleSelect && now.Options.Count == options.Count
            && now.Options.Zip(options).All(p => p.First.Title == p.Second.Title && p.First.RoleIds.SequenceEqual(p.Second.RoleIds ?? []) && p.First.Emoji?.Name == p.Second.EmojiName);
    }

    // Another question, sent back as it is.
    private static GuildOnboardingPromptProperties Copy(GuildOnboardingPrompt prompt)
        => new(prompt.Type, prompt.Options.Select(o => new GuildOnboardingPromptOptionProperties(o.ChannelIds, o.RoleIds, o.Title)
        {
            Id = o.Id,
            Description = o.Description,
            EmojiId = o.Emoji is GuildEmoji custom ? custom.Id : null,
            EmojiName = o.Emoji?.Name,
            EmojiAnimated = o.Emoji is GuildEmoji animated ? animated.Animated : null,
        }).ToList(), prompt.Title)
        {
            Id = prompt.Id,
            SingleSelect = prompt.SingleSelect,
            Required = prompt.Required,
            InOnboarding = prompt.InOnboarding,
        };

    private static string Clip(string text) => text.Length <= TitleLength ? text : text[..(TitleLength - 1)] + "…";
}

// What the onboarding question is doing, at the top of the Games settings page.
public sealed class GameOnboardingSummary(SettingsStore settings, GatewayClient gateway, GameDirectory games) : ISettingsSummary
{
    public string ModuleId => GameDirectory.ModuleId;

    public async Task<string?> NowAsync(ulong guildId, Func<ulong, string> channel)
    {
        var rules = await settings.GetAsync<GameRules>(guildId, ModuleId);
        if (!rules.InOnboarding)
            return null;
        return !gateway.IsCommunity(guildId)
            ? $"Now: the onboarding question waits for this to be {Community.Needed}: Discord has no onboarding before that."
            : $"Now: \"{rules.OnboardingQuestion}\" in Discord's onboarding offers {(await games.ListAsync(guildId)).Count} games.";
    }
}
