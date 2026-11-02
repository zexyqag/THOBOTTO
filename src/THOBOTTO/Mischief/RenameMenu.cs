using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;
using THOBOTTO.Points;

namespace THOBOTTO.Mischief;

// Right-click a member, Apps, Rename them: /rename, asking the name and why.
public sealed class RenameMenu(
    MischiefActions actions,
    ModuleState modules,
    SettingsStore settings,
    PointsEngine points,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time) : MischiefModule(actions, modules, settings, points, dbFactory, time)
{
    [UserCommand("Rename them", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> RenameAsync(User user)
        => await ModuleOffAsync() is { } off
            ? InteractionCallback.Message(off)
            : InteractionCallback.Modal(new ModalProperties($"mischiefrename:{user.Id}", "Rename them")
            {
                new LabelProperties("New nickname (empty: back to their own)", new TextInputProperties("name", TextInputStyle.Short)
                {
                    Value = (user as GuildUser)?.Nickname,
                    MaxLength = 32,
                    Required = false,
                }),
                new LabelProperties("Why (optional)", new TextInputProperties("reason", TextInputStyle.Short) { MaxLength = 200, Required = false }),
            });
}

// As /rename: refusals privately, the rename announced in the channel.
public sealed class RenameMenuModal(MischiefActions actions, RestClient rest) : GuildModalModule
{
    [ComponentInteraction("mischiefrename")]
    public async Task RenameAsync(ulong userId)
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        GuildUser member;
        try
        {
            member = Guild.Users.TryGetValue(userId, out var cached) ? cached : await rest.GetGuildUserAsync(GuildId, userId);
        }
        catch (RestException)
        {
            await ModifyResponseAsync(m => m.Content = "They're not in the server any more.");
            return;
        }
        var result = await actions.RenameAsync(new(GuildId, Context.User.Id, Context.User.Username, Context.Channel.Id), member,
            inputs.GetValueOrDefault("name") is { Length: > 0 } name ? name : null, inputs.GetValueOrDefault("reason") is { Length: > 0 } reason ? reason : null);
        if (!result.Public)
        {
            await ModifyResponseAsync(m => m.Content = result.Text);
            return;
        }
        await Context.Channel.SendMessageAsync(new() { Content = result.Text, AllowedMentions = AllowedMentionsProperties.None });
        await DeleteResponseAsync();
    }
}
