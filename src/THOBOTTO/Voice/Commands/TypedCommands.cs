using System.Text.RegularExpressions;

using NetCord.Gateway;
using NetCord.Hosting.Gateway;

using THOBOTTO.Discord;
using THOBOTTO.Helpers;
using THOBOTTO.Modules;
using THOBOTTO.Voice.Hearing;

namespace THOBOTTO.Voice.Commands;

// A message to a helper ("Jeeves, skip", "Jeeves: what's playing", or one starting with its mention), read as a voice
// command would be and answered there by that helper, in its personality; where it acts (the music, who's around) is
// the voice channel the member is in, if any. Nothing is listened to: they wrote it to the helper.
public sealed partial class TypedCommands(VoiceCommands commands, HelperFleet fleet, PersonalityBook personalities, VoicePresence presence, ModuleState modules, SettingsStore settings)
    : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        if (message is not { GuildId: { } guildId, WebhookId: null, Author.IsBot: false } || message.Content.Length < 3
            || !await modules.IsEnabledAsync(guildId, VoiceEars.ModuleId) || !(await settings.GetAsync<ListeningRules>(guildId, VoiceEars.ModuleId)).Typed)
            return;
        var helpers = fleet.Helpers.Where(h => h.InGuild(guildId)).ToList();
        if (await AddressedAsync(guildId, helpers, message.Content) is not var (helper, said) || said.Length == 0)
            return;
        var voice = presence.Snapshot(guildId).TryGetValue(message.Author.Id, out var there) ? there.ChannelId : 0;
        await commands.OnTypedAsync(new Heard(guildId, voice, message.Author.Id, helper.UserId, said, TypedIn: message.ChannelId), helper, said);
    }

    private async Task<(HelperBot Helper, string Said)?> AddressedAsync(ulong guildId, IReadOnlyList<HelperBot> helpers, string text)
    {
        if (Mention().Match(text) is { Success: true } mention && helpers.FirstOrDefault(h => h.UserId == ulong.Parse(mention.Groups[1].Value)) is { } mentioned)
            return (mentioned, text[mention.Length..].Trim(' ', ',', ':'));
        foreach (var helper in helpers)
        {
            foreach (var name in (await personalities.AnswersToAsync(guildId, helper)).OrderByDescending(n => n.Length))
            {
                if (Addressed(text, name) is { } said)
                    return (helper, said);
            }
        }
        return null;
    }

    // What follows "Name," or "Name:" (in any case); a sentence that merely starts with the name isn't to it.
    public static string? Addressed(string text, string name)
        => text.Length > name.Length + 1 && text.StartsWith(name, StringComparison.OrdinalIgnoreCase) && text[name.Length] is ',' or ':'
            ? text[(name.Length + 1)..].Trim()
            : null;

    [GeneratedRegex(@"^<@!?(\d+)>")]
    private static partial Regex Mention();
}
