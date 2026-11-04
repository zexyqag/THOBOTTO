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

// Right-click a message with an image, a sound file or custom emoji from elsewhere, Apps: put it up for the server's
// vote as an emoji, a sticker or a sound, named in a form.
public static partial class ProposeMenu
{
    public const string Sound = "sound";
    // An emoji used in the message, picked as emoji:id:animated:name.
    public const string Used = "emoji:";

    // The custom emoji in a message that this server doesn't have on Discord's list.
    public static IEnumerable<(ulong Id, bool Animated, string Name)> Foreign(string content, IReadOnlyDictionary<ulong, GuildEmoji> own)
        => CustomEmoji().Matches(content).Select(m => (Id: ulong.Parse(m.Groups[3].Value), Animated: m.Groups[1].Length > 0, Name: m.Groups[2].Value))
            .Where(e => !own.ContainsKey(e.Id)).DistinctBy(e => e.Id);

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

    [GeneratedRegex(@"<(a?):(\w{2,32}):(\d+)>")]
    private static partial Regex CustomEmoji();
}

public sealed class ProposeMenuCommand(ModuleState modules) : GuildCommandModule
{
    [MessageCommand("Propose as emoji/sticker/sound", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> ProposeAsync(RestMessage message)
    {
        var image = message.Attachments.FirstOrDefault(ProposeMenu.IsImage);
        var audio = message.Attachments.FirstOrDefault(ProposeMenu.IsAudio);
        var used = ProposeMenu.Foreign(message.Content, Guild.Emojis).Take(20).ToList();
        var kinds = new List<(string Value, string Label)>();
        if (await modules.IsEnabledAsync(GuildId, ExpressionShelf.ModuleId))
        {
            kinds.AddRange(used.Select(e => ($"{ProposeMenu.Used}{e.Id}:{(e.Animated ? 1 : 0)}:{e.Name}", $"The emoji :{e.Name}: from the message")));
            if (image is not null)
                kinds.AddRange([(ExpressionKinds.Emoji, "An emoji of the image"), (ExpressionKinds.Sticker, "A sticker of the image")]);
        }
        if (audio is not null && await modules.IsEnabledAsync(GuildId, SoundBoard.ModuleId))
            kinds.Add((ProposeMenu.Sound, "A sound"));
        if (kinds.Count == 0)
            return InteractionCallback.Message(Replies.Ephemeral(image is null && audio is null && used.Count == 0
                ? "Right-click a message with an image (for an emoji or sticker), a sound file, or emoji from another server."
                : "Emojis, stickers and sounds aren't on in this server."));

        var form = new ModalProperties($"proposefile:{message.ChannelId}:{message.Id}", "Propose for a vote")
        {
            new LabelProperties("As", new StringMenuProperties("kind", kinds.Select((k, i) => new StringMenuSelectOptionProperties(k.Label, k.Value) { Default = i == 0 }))),
            new LabelProperties(used.Count > 0 ? "Its name (empty: the one it has)" : "Its name (letters, digits and _)", new TextInputProperties("name", TextInputStyle.Short)
            {
                Value = used.Count > 0 ? null : ProposeMenu.NameOf((image ?? audio)!.FileName) is { Length: > 0 } name ? name : null,
                MinLength = used.Count > 0 ? 0 : 2,
                MaxLength = 32,
                Required = used.Count == 0,
            }),
        };
        if (kinds.Any(k => k.Value == ExpressionKinds.Sticker))
            form.Add(new LabelProperties("A sticker's emoji (stickers only)", new TextInputProperties("tag", TextInputStyle.Short) { Value = "⭐", MaxLength = 50, Required = false }));
        return InteractionCallback.Modal(form);
    }
}

public sealed class ProposeMenuModal(ExpressionShelf shelf, ExpressionPictures pictures, SoundBoard board, RestClient rest) : GuildModalModule
{
    [ComponentInteraction("proposefile")]
    public async Task ProposeAsync(ulong channelId, ulong messageId)
    {
        var parts = Context.Components.OfType<Label>().Select(l => l.Component).ToList();
        var kind = parts.OfType<StringMenu>().FirstOrDefault()?.SelectedValues is [var picked, ..] ? picked : ExpressionKinds.Emoji;
        var text = parts.OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value.Trim());
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        string result;
        if (kind.StartsWith(ProposeMenu.Used, StringComparison.Ordinal) && kind[ProposeMenu.Used.Length..].Split(':', 3) is [var id, var animated, var called])
        {
            var name = text.GetValueOrDefault("name") is { Length: > 0 } given ? given : called;
            result = await pictures.EmojiAsync(ulong.Parse(id), animated == "1", 128) is { } picture
                ? await shelf.ProposeAsync(GuildId, ExpressionKinds.Emoji, name, null, Context.User.Id, picture, animated == "1" ? "image/gif" : "image/png", Context.Channel.Id)
                : "That emoji's picture couldn't be fetched.";
            await ModifyResponseAsync(m => m.Content = result);
            return;
        }
        try
        {
            var message = await rest.GetMessageAsync(channelId, messageId);
            var file = message.Attachments.FirstOrDefault(kind == ProposeMenu.Sound ? ProposeMenu.IsAudio : ProposeMenu.IsImage);
            var named = file is null ? "" : text.GetValueOrDefault("name") is { Length: > 0 } given ? given : ProposeMenu.NameOf(file.FileName);
            result = file is null ? "That file is gone from the message."
                : kind == ProposeMenu.Sound ? await board.AddAsync(GuildId, named, Context.User.Id, file, direct: false, Context.Channel.Id)
                : await shelf.ProposeAsync(GuildId, kind, named, kind == ExpressionKinds.Sticker ? text.GetValueOrDefault("tag") is { Length: > 0 } tag ? tag : "⭐" : null,
                    Context.User.Id, file, Context.Channel.Id);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            result = "That message is gone.";
        }
        await ModifyResponseAsync(m => m.Content = result);
    }
}
