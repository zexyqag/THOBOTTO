using System.Net;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;
using THOBOTTO.Expressions;
using THOBOTTO.Modules;

namespace THOBOTTO.Sounds;

// Right-click a message with an image or a sound file, Apps: put it up for the server's vote as an emoji, a sticker or
// a sound, named in a form.
public static partial class ProposeMenu
{
    public const string Sound = "sound";

    public static bool IsImage(Attachment a) => a.ContentType?.StartsWith("image/", StringComparison.Ordinal) == true;

    public static bool IsAudio(Attachment a) => a.ContentType?.StartsWith("audio/", StringComparison.Ordinal) == true
        || Path.GetExtension(a.FileName).ToLowerInvariant() is ".mp3" or ".ogg" or ".wav";

    // A name from the file's: letters, digits and _, as emoji names take.
    public static string NameOf(string fileName)
    {
        var name = Unfit().Replace(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), "_").Trim('_');
        return name.Length > 32 ? name[..32] : name.Length < 2 ? "" : name;
    }

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex Unfit();
}

public sealed class ProposeMenuCommand(ModuleState modules) : GuildCommandModule
{
    [MessageCommand("Propose as emoji/sticker/sound", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> ProposeAsync(RestMessage message)
    {
        var image = message.Attachments.FirstOrDefault(ProposeMenu.IsImage);
        var audio = message.Attachments.FirstOrDefault(ProposeMenu.IsAudio);
        var kinds = new List<(string Value, string Label)>();
        if (image is not null && await modules.IsEnabledAsync(GuildId, ExpressionShelf.ModuleId))
            kinds.AddRange([(ExpressionKinds.Emoji, "An emoji"), (ExpressionKinds.Sticker, "A sticker")]);
        if (audio is not null && await modules.IsEnabledAsync(GuildId, SoundBoard.ModuleId))
            kinds.Add((ProposeMenu.Sound, "A sound"));
        if (kinds.Count == 0)
            return InteractionCallback.Message(Replies.Ephemeral(image is null && audio is null
                ? "Right-click a message with an image (for an emoji or sticker) or a sound file."
                : "Emojis, stickers and sounds aren't on in this server."));

        var form = new ModalProperties($"proposefile:{message.ChannelId}:{message.Id}", "Propose for a vote")
        {
            new LabelProperties("As", new StringMenuProperties("kind", kinds.Select((k, i) => new StringMenuSelectOptionProperties(k.Label, k.Value) { Default = i == 0 }))),
            new LabelProperties("Its name (letters, digits and _)", new TextInputProperties("name", TextInputStyle.Short)
            {
                Value = ProposeMenu.NameOf((image ?? audio)!.FileName) is { Length: > 0 } name ? name : null,
                MinLength = 2,
                MaxLength = 32,
            }),
        };
        if (kinds.Any(k => k.Value == ExpressionKinds.Sticker))
            form.Add(new LabelProperties("A sticker's emoji (stickers only)", new TextInputProperties("tag", TextInputStyle.Short) { Value = "⭐", MaxLength = 50, Required = false }));
        return InteractionCallback.Modal(form);
    }
}

public sealed class ProposeMenuModal(ExpressionShelf shelf, SoundBoard board, RestClient rest) : GuildModalModule
{
    [ComponentInteraction("proposefile")]
    public async Task ProposeAsync(ulong channelId, ulong messageId)
    {
        var parts = Context.Components.OfType<Label>().Select(l => l.Component).ToList();
        var kind = parts.OfType<StringMenu>().FirstOrDefault()?.SelectedValues is [var picked, ..] ? picked : ExpressionKinds.Emoji;
        var text = parts.OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        try
        {
            var message = await rest.GetMessageAsync(channelId, messageId);
            var file = message.Attachments.FirstOrDefault(kind == ProposeMenu.Sound ? ProposeMenu.IsAudio : ProposeMenu.IsImage);
            result = file is null ? "That file is gone from the message."
                : kind == ProposeMenu.Sound ? await board.AddAsync(GuildId, text["name"], Context.User.Id, file, direct: false, Context.Channel.Id)
                : await shelf.ProposeAsync(GuildId, kind, text["name"], kind == ExpressionKinds.Sticker ? text.GetValueOrDefault("tag") is { Length: > 0 } tag ? tag : "⭐" : null,
                    Context.User.Id, file, Context.Channel.Id);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            result = "That message is gone.";
        }
        await ModifyResponseAsync(m => m.Content = result);
    }
}
