using System.Text;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Discord;
using THOBOTTO.Integrations;
using THOBOTTO.Modules;
using THOBOTTO.Panel;

namespace THOBOTTO.Steam;

[SlashCommand("steam", "Steam: who owns a game, and what you can all play together", Contexts = [InteractionContextType.Guild])]
public sealed partial class SteamCommands(SteamLibrary library, SteamClient client, ModuleState modules, PanelLinks links, IntegrationStore store) : GuildCommandModule
{
    public const string NotSetUp = "Steam isn't set up: the bot's owner sets a Steam Web API key on the panel's Integrations page.";
    private const int Shown = 15;
    // The store is asked about at most this many games a time; the rest wait for the next.
    private const int StorePagesAtOnce = 25;

    [SubSlashCommand("link", "Link your Steam account by signing in through Steam (only you see the link)")]
    public async Task<InteractionMessageProperties> LinkAsync()
    {
        if (await RefusedAsync() is { } refused)
            return Replies.Ephemeral(refused);
        if (await links.AddressAsync() is not { } address)
            return Replies.Ephemeral("The panel's address isn't known, and signing in through Steam happens there. The bot's owner adds it as a redirect on the bot's OAuth2 page.");
        // Each link logs in to the panel once, then goes on to the sign-in.
        string Link(string after)
            => $"{address}/login/link?token={links.Issue(Context.User.Id, Context.User.GlobalName ?? Context.User.Username, GuildId)}&back={Uri.EscapeDataString($"{after}?back={Uri.EscapeDataString($"/g/{GuildId}/me")}")}";
        var ways = $"[Sign in through Steam]({Link("/steam/connect")})"
            + (await DiscordLogin.AvailableAsync(store, links) ? $" or [use the Steam account on your Discord profile]({Link($"/login/for/{SteamFromDiscord.Purpose}")})" : "");
        var linked = await library.FindAsync(Context.User.Id);
        var reply = Replies.Ephemeral(linked is null
            ? $"{ways} to link your account: members of servers with Steam on see which games you own (with your game details public on Steam). The links work once, within 10 minutes."
            : $"You're linked to **{linked.Name}**. {ways} to link another account, or `/steam unlink`.");
        reply.Flags |= MessageFlags.SuppressEmbeds;
        return reply;
    }

    [SubSlashCommand("unlink", "Unlink your Steam account: your games aren't shown any more")]
    public async Task<InteractionMessageProperties> UnlinkAsync()
    {
        if (await library.FindAsync(Context.User.Id) is null)
            return Replies.Ephemeral("Your Steam account isn't linked.");
        await library.UnlinkAsync(Context.User.Id);
        return Replies.Ephemeral("Unlinked: your Steam games aren't shown any more, in any server.");
    }

    [SubSlashCommand("owns", "Who here owns a game on Steam")]
    public async Task<InteractionMessageProperties> OwnsAsync(
        [SlashCommandParameter(Description = "The game", AutocompleteProviderType = typeof(SteamGameAutocomplete))] int game)
    {
        if (await RefusedAsync() is { } refused)
            return Replies.Ephemeral(refused);
        var members = Members(Guild);
        var linked = await library.LinkedAsync(members);
        var (name, owners) = await library.OwnersAsync(game, members);
        if (name is null)
            return Replies.Ephemeral("Nobody here who's linked Steam owns that. Pick a game from the list.");
        var text = new StringBuilder($"**{name}** on Steam\n");
        text.AppendJoin('\n', owners.Select(o => $"<@{o.UserId}> · {Played(o.Minutes)}"));
        if (owners.Count == 0)
            text.Append("Nobody here who's linked Steam owns it.");
        text.Append($"\n-# {owners.Count} of the {linked.Count} here who've linked Steam · `/steam link` to add yours");
        return new() { Content = text.ToString(), AllowedMentions = AllowedMentionsProperties.None };
    }

    [SubSlashCommand("common", "The games everyone in your voice channel (or those you name) owns and can play together")]
    public async Task CommonAsync(
        [SlashCommandParameter(Description = "Who, besides you, by mentioning them (if not given: everyone in your voice channel)")] string? with = null)
    {
        if (await RefusedAsync() is { } refused)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(refused)));
            return;
        }
        HashSet<ulong> who;
        if (with is not null)
            who = [.. Mention().Matches(with).Select(m => ulong.Parse(m.Groups[1].Value)), Context.User.Id];
        else if (Guild.VoiceStates.TryGetValue(Context.User.Id, out var mine) && mine.ChannelId is { } channel)
            who = [.. Guild.VoiceStates.Values.Where(v => v.ChannelId == channel).Select(v => v.UserId).Where(id => !(Guild.Users.TryGetValue(id, out var user) && user.IsBot))];
        else
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Join a voice channel with the others, or name them: `with: @Ana @Mads`.")));
            return;
        }
        if (who.Count < 2)
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("That's only you: name at least one other member, or join a voice channel with them.")));
            return;
        }
        // Asking the store about the games can take longer than Discord waits.
        await RespondAsync(InteractionCallback.DeferredMessage());
        var linked = await library.LinkedAsync(who);
        var readable = linked.Where(l => !l.Hidden).Select(l => l.UserId).ToHashSet();
        var notes = new List<string>();
        if (who.Where(id => linked.All(l => l.UserId != id)).ToList() is { Count: > 0 } unlinked)
            notes.Add($"{Names(unlinked)} {(unlinked.Count == 1 ? "hasn't" : "haven't")} linked Steam (`/steam link`)");
        if (linked.Where(l => l.Hidden).Select(l => l.UserId).ToList() is { Count: > 0 } hidden)
            notes.Add($"{Names(hidden)} {(hidden.Count == 1 ? "has" : "have")} their game details private on Steam");
        var text = new StringBuilder($"🎮 **What you can all play** ({Names(readable)})\n");
        if (readable.Count < 2)
            text.Append("Too few of you have games to compare.");
        else
        {
            var shared = await library.SharedAsync(readable);
            var together = new List<SharedGame>();
            var asked = 0;
            foreach (var game in shared)
            {
                if (together.Count == Shown || asked == StorePagesAtOnce)
                    break;
                asked++;
                if (await library.TogetherAsync(game.AppId) != false)
                    together.Add(game);
            }
            text.AppendJoin('\n', together.Select(g => $"**{g.Name}** · {Played((int)Math.Min(g.Minutes, int.MaxValue))} between you"));
            if (together.Count == 0)
                text.Append(shared.Count == 0 ? "No game that all of you own." : "None of the games you all own is played together, as far as Steam's store says.");
            if (shared.Count > asked)
                text.Append($"\n…and {shared.Count - asked} more you all own.");
            notes.Insert(0, "Games for more than one player on Steam that all of you own");
        }
        text.Append($"\n-# {string.Join("; ", notes)}.");
        await ModifyResponseAsync(m => (m.Content, m.AllowedMentions) = (text.ToString(), AllowedMentionsProperties.None));
    }

    private async Task<string?> RefusedAsync()
        => !await modules.IsEnabledAsync(GuildId, SteamLibrary.ModuleId) ? ModuleRegistry.Off(SteamLibrary.ModuleId)
            : !client.Configured ? NotSetUp
            : null;

    public static IReadOnlyCollection<ulong> Members(Guild guild) => [.. guild.Users.Values.Where(u => !u.IsBot).Select(u => u.Id)];

    private static string Names(IEnumerable<ulong> ids) => string.Join(", ", ids.Select(id => $"<@{id}>"));

    public static string Played(int minutes) => minutes switch
    {
        0 => "not played yet",
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60:N0} h",
    };

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex Mention();
}

public sealed class SteamGameAutocomplete(SteamLibrary library) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        if (context.Guild is not { } guild)
            return [];
        return (await library.GamesAsync(SteamCommands.Members(guild), option.Value ?? "", 25))
            .Select(g => new ApplicationCommandOptionChoiceProperties(g.Name.Length <= 100 ? g.Name : g.Name[..100], g.AppId));
    }
}
