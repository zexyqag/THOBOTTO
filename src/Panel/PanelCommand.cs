using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace THOBOTTO.Panel;

public sealed class PanelCommand(PanelLinks links) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("panel", "A link that logs you in to the web panel (only you see it)")]
    public async Task<InteractionMessageProperties> PanelAsync()
    {
        if (await links.AddressAsync() is not { } address)
            return Replies.Ephemeral($"The panel's address isn't known: add `https://<your panel>{PanelLinks.Callback}` as a redirect on the bot's OAuth2 page in the Discord Developer Portal.");
        var token = links.Issue(Context.User.Id, Context.User.GlobalName ?? Context.User.Username, Context.Guild?.Id);
        return Replies.Ephemeral($"[Open the panel]({address}/login/link?token={token}): the link logs you in once, within 10 minutes. Don't share it.");
    }
}
