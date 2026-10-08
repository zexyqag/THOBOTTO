using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Modules;

namespace THOBOTTO.Expressions;

[SlashCommand("emoji", "Member-made emojis", Contexts = [InteractionContextType.Guild])]
public sealed class EmojiCommands(ExpressionShelf shelf, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("propose", "Put an emoji up for a vote")]
    public Task ProposeAsync(
        [SlashCommandParameter(Description = "Name, e.g. thobotto_cry (letters, digits, _)", MinLength = 2, MaxLength = 32)] string name,
        [SlashCommandParameter(Description = "PNG, JPEG, GIF or WebP, at most 256 KB")] Attachment image)
        => DeferredAsync(this, modules, () => shelf.ProposeAsync(Context.Guild!.Id, ExpressionKinds.Emoji, name, null, Context.User.Id, image));

    [SubSlashCommand("revive", "Put a retired emoji up for a vote again")]
    public Task ReviveAsync([SlashCommandParameter(Description = "Its name")] string name)
        => DeferredAsync(this, modules, () => shelf.ReviveAsync(Context.Guild!.Id, ExpressionKinds.Emoji, name, Context.User.Id));

    [SubSlashCommand("list", "Member-made emojis and stickers, with how much they're used")]
    public async Task<InteractionMessageProperties> ListAsync()
    {
        if (await ModuleOffAsync(modules, Context) is { } off)
            return off;

        var all = await shelf.ListAsync(Context.Guild!.Id);
        if (all.Count == 0)
            return Replies.Ephemeral("None yet. Propose one with `/emoji propose` or `/sticker propose`.");

        var lines = all.GroupBy(e => e.State).Select(g => $"**{g.Key}**\n" + string.Join('\n', g.Select(e =>
            $"{(e.Kind == ExpressionKinds.Emoji ? (e.DiscordId is { } id ? $"<{(e.Animated ? "a" : "")}:{e.Name}:{id}>" : $":{e.Name}:") : $"sticker {e.Name}")} by <@{e.CreatorId}>"
            + (e.State == ExpressionStates.Live ? $", used {e.Uses}×" : ""))));
        return Replies.Ephemeral(string.Join("\n\n", lines));
    }

    // Proposals download the image and post the vote, which can outlast Discord's wait for a reply.
    internal static async Task DeferredAsync(ApplicationCommandModule<ApplicationCommandContext> module, ModuleState modules, Func<Task<string>> work)
    {
        if (await ModuleOffAsync(modules, module.Context) is { } off)
        {
            await module.RespondAsync(InteractionCallback.Message(off));
            return;
        }

        await module.RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await work();
        await module.ModifyResponseAsync(m => m.Content = result);
    }

    internal static async Task<InteractionMessageProperties?> ModuleOffAsync(ModuleState modules, ApplicationCommandContext context)
        => await modules.IsEnabledAsync(context.Guild!.Id, ExpressionShelf.ModuleId) ? null : Replies.Ephemeral($"The `{ExpressionShelf.ModuleId}` module is off.");
}

[SlashCommand("sticker", "Member-made stickers", Contexts = [InteractionContextType.Guild])]
public sealed class StickerCommands(ExpressionShelf shelf, ModuleState modules) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("propose", "Put a sticker up for a vote")]
    public Task ProposeAsync(
        [SlashCommandParameter(Description = "Name", MinLength = 2, MaxLength = 30)] string name,
        [SlashCommandParameter(Description = "PNG, APNG or GIF, at most 512 KB (320×320 works best)")] Attachment image,
        [SlashCommandParameter(Description = "The emoji it's suggested for, e.g. 😂", MaxLength = 50)] string tag = "⭐")
        => EmojiCommands.DeferredAsync(this, modules, () => shelf.ProposeAsync(Context.Guild!.Id, ExpressionKinds.Sticker, name.Trim(), tag, Context.User.Id, image));

    [SubSlashCommand("revive", "Put a retired sticker up for a vote again")]
    public Task ReviveAsync([SlashCommandParameter(Description = "Its name")] string name)
        => EmojiCommands.DeferredAsync(this, modules, () => shelf.ReviveAsync(Context.Guild!.Id, ExpressionKinds.Sticker, name.Trim(), Context.User.Id));
}

public sealed class ExpressionVoteButtons(ExpressionShelf shelf) : ComponentInteractionModule<ButtonInteractionContext>
{
    // Deferred: an accepting vote uploads the emoji, which can take longer than Discord waits for a reply.
    [ComponentInteraction("exvote")]
    public async Task VoteAsync(long id, int up)
    {
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var result = await shelf.VoteAsync(id, Context.User.Id, up == 1);
        await ModifyResponseAsync(m => m.Content = result);
    }
}
