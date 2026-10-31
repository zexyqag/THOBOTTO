using System.Text;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Live;

[SlashCommand("live", "Live alerts: Twitch channels the server follows, besides members' own streams", Contexts = [InteractionContextType.Guild])]
public sealed class LiveCommands(TwitchWatch twitch, ModuleState modules) : GuildCommandModule
{
    [SubSlashCommand("follow", "Follow a Twitch channel: it's announced when it goes live, member or not")]
    [RequirePermission(BotPermissions.ManageLive)]
    public async Task FollowAsync([SlashCommandParameter(Description = "The channel: its name or link", MaxLength = 100)] string channel)
    {
        if (!await modules.IsEnabledAsync(GuildId, LiveWatch.ModuleId))
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral(ModuleRegistry.OffAndHowToTurnOn(LiveWatch.ModuleId))));
            return;
        }
        // Twitch is asked about it, which can take longer than Discord waits.
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var (followed, problem) = await twitch.FollowAsync(GuildId, channel, Context.User.Id);
        await ModifyResponseAsync(m => m.Content = followed is null ? problem : $"Following **{followed.Name}** on Twitch: it's announced when it goes live (within a minute or so), where members' streams are.");
    }

    [SubSlashCommand("unfollow", "Stop following a Twitch channel")]
    [RequirePermission(BotPermissions.ManageLive)]
    public async Task<InteractionMessageProperties> UnfollowAsync(
        [SlashCommandParameter(Description = "The channel", AutocompleteProviderType = typeof(TwitchChannelAutocomplete))] long channel)
        => await twitch.UnfollowAsync(GuildId, channel) is { } gone
            ? Replies.Ephemeral($"No longer following **{gone.Name}**.")
            : Replies.Ephemeral("This server doesn't follow that channel. Pick one from the list.");

    [SubSlashCommand("list", "The Twitch channels this server follows")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        var followed = await twitch.FollowedAsync(GuildId);
        if (followed.Count == 0)
            return Replies.Ephemeral("This server follows no Twitch channels: `/live follow`.");
        var text = new StringBuilder();
        foreach (var channel in followed)
            text.AppendLine($"**{channel.Name}** · <{TwitchClient.Url(channel.Login)}>");
        return Replies.Ephemeral(text.ToString());
    }
}

public sealed class TwitchChannelAutocomplete(TwitchWatch twitch) : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public async ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option,
        AutocompleteInteractionContext context)
    {
        var input = option.Value ?? "";
        return (await twitch.FollowedAsync(context.Interaction.GuildId!.Value))
            .Where(c => c.Name.Contains(input, StringComparison.OrdinalIgnoreCase) || c.Login.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(c => new ApplicationCommandOptionChoiceProperties(c.Name, c.Id));
    }
}
