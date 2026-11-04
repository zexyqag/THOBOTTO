using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Kaomoji;

// Right-click a message, Apps, Save as kaomoji: its face (guessed, and editable) added to the server's own.
public sealed class SaveAsKaomojiCommand(KaomojiShelf shelf, ModuleState modules) : GuildCommandModule
{
    [MessageCommand("Save as kaomoji", Contexts = [InteractionContextType.Guild])]
    public async Task<InteractionCallbackProperties> SaveAsync(RestMessage message)
    {
        if (!await modules.IsEnabledAsync(GuildId, KaomojiShelf.ModuleId))
            return InteractionCallback.Message(Replies.Ephemeral(ModuleRegistry.Off(KaomojiShelf.ModuleId)));
        if (string.IsNullOrWhiteSpace(message.Content))
            return InteractionCallback.Message(Replies.Ephemeral("That message has no text to take a face from."));
        var (face, known) = KaomojiLibrary.Guess(message.Content, await shelf.InAsync(GuildId));
        return InteractionCallback.Modal(new ModalProperties("savekaomoji", "Save as kaomoji")
        {
            new LabelProperties("The face (keep only the face)", new TextInputProperties("text", TextInputStyle.Short) { Value = face.Length <= KaomojiShelf.TextLength ? face : face[..KaomojiShelf.TextLength], MaxLength = KaomojiShelf.TextLength }),
            new LabelProperties("Its name", new TextInputProperties("name", TextInputStyle.Short) { Value = known?.Name, MaxLength = KaomojiShelf.NameLength }),
            new LabelProperties("Other words that find it (comma separated)", new TextInputProperties("tags", TextInputStyle.Short) { Required = false, MaxLength = KaomojiShelf.TagsLength }),
        });
    }
}

public sealed class SaveAsKaomojiModal(KaomojiShelf shelf) : GuildModalModule
{
    [ComponentInteraction("savekaomoji")]
    public async Task<InteractionCallbackProperties> SaveAsync()
    {
        var inputs = Context.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().ToDictionary(t => t.CustomId, t => t.Value);
        var text = inputs.GetValueOrDefault("text") ?? "";
        return InteractionCallback.Message(Replies.Ephemeral(await shelf.AddAsync(GuildId, Context.User.Id, text, inputs.GetValueOrDefault("name") ?? "", inputs.GetValueOrDefault("tags") ?? "") is { } refused
            ? refused
            : $"Saved: {KaomojiLibrary.Escaped(text.Trim())} is one of the server's own now, offered first in /kaomoji."));
    }
}
