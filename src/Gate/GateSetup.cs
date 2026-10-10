using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Gate;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("gate", "The waiting room for new members (needs mod.manage)")]
    [RequirePermission(BotPermissions.ModManage)]
    public sealed class GateSetup(Gatekeeper gate) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("prepare", "Make the waiting role and channel (only they see it), and post the rules there")]
        public async Task PrepareAsync(
            [SlashCommandParameter(Description = "An existing channel to use as the waiting room", AllowedChannelTypes = [ChannelType.TextGuildChannel])] Channel? channel = null)
        {
            // Changing every channel takes a while.
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            string result;
            try
            {
                result = await gate.PrepareAsync(Context.Guild!, channel?.Id, Context.User.Id);
            }
            catch (RestException ex)
            {
                result = $"Discord refused: {ex.Message}. I need Manage Roles and Manage Channels.";
            }
            await ModifyResponseAsync(m => m.Content = result);
        }

        [SubSlashCommand("post", "Post the rules and button in the waiting channel again")]
        public async Task<InteractionMessageProperties> PostAsync() => Replies.Ephemeral(await gate.PostVerifyAsync(Context.Guild!.Id));
    }
}
