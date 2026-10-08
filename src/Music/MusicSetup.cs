
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Modules;
using THOBOTTO.Music;

namespace THOBOTTO;

public sealed partial class SetupCommands
{
    [SubSlashCommand("music", "Music: idle time, queue size, search source, DJ rule (needs Get<MusicService>().manage)")]
    [RequirePermission(BotPermissions.ManageMusic)]
    public async Task<InteractionMessageProperties> MusicAsync(
        [SlashCommandParameter(Name = "idle-minutes", Description = "Leave after this long with nobody listening or nothing playing", MinValue = 1, MaxValue = 120)] int? idleMinutes = null,
        [SlashCommandParameter(Name = "max-queue", Description = "Most tracks in a queue", MinValue = 1, MaxValue = 5000)] int? maxQueue = null,
        [SlashCommandParameter(Name = "search", Description = "Where plain words are searched")] SearchSource? search = null,
        [SlashCommandParameter(Name = "dj-only", Description = "Skip, stop, pause and volume need Get<MusicService>().dj (your own track you can always skip)")] bool? djOnly = null)
    {
        var guildId = Context.Guild!.Id;
        var before = await Get<SettingsStore>().GetAsync<MusicRules>(guildId, MusicService.ModuleId);
        var after = before with
        {
            IdleMinutes = idleMinutes ?? before.IdleMinutes,
            MaxQueue = maxQueue ?? before.MaxQueue,
            DefaultSearch = search switch { SearchSource.YouTube => "ytsearch", SearchSource.SoundCloud => "scsearch", _ => before.DefaultSearch },
            DjOnly = djOnly ?? before.DjOnly,
        };
        var changed = after != before;
        if (changed)
            await Get<SettingsStore>().SetAsync(guildId, MusicService.ModuleId, after, Context.User.Id, SettingsStore.Diff(before, after));

        return Replies.Ephemeral($"""
            {(changed ? "Updated." : "Nothing changed.")}
            Leave after {after.IdleMinutes} min idle · queue up to {after.MaxQueue} · search {(after.DefaultSearch == "scsearch" ? "SoundCloud" : "YouTube")} · controls: {(after.DjOnly ? $"`{BotPermissions.MusicDj}` only" : "anyone")}
            """);
    }

    [SubSlashCommand("helpers", "The music helpers' characters (needs music.manage)")]
    [RequirePermission(BotPermissions.ManageMusic)]
    public sealed class HelperSetup(MusicService music, Personalities personalities) : ApplicationCommandModule<ApplicationCommandContext>
    {
        [SubSlashCommand("personality", "Nickname, colour, avatar")]
        public async Task PersonalityAsync(
            [SlashCommandParameter(Description = "Helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper,
            [SlashCommandParameter(Description = "Name in servers; \"none\" for its account name", MaxLength = 32)] string? nickname = null,
            [SlashCommandParameter(Description = "Colour of its messages, e.g. #ff3b7f", MaxLength = 7)] string? colour = null,
            [SlashCommandParameter(Description = "New avatar (changes the account everywhere; Discord limits how often)")] Attachment? avatar = null)
        {
            if (Find(helper) is not { } bot)
            {
                await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("There's no such helper.")));
                return;
            }
            int? rgb = null;
            if (colour is not null)
            {
                if (!int.TryParse(colour.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var parsed) || colour.TrimStart('#').Length != 6)
                {
                    await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("Colours are hex, like `#ff3b7f`.")));
                    return;
                }
                rgb = parsed;
            }

            // Nicknames go to every server and avatars upload; that takes a moment.
            await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
            if (nickname is not null || rgb is not null)
            {
                await personalities.SaveAsync(bot.UserId, p =>
                {
                    p.Nickname = nickname is null ? p.Nickname : nickname.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : nickname.Trim();
                    p.Color = rgb ?? p.Color;
                });
                if (nickname is not null)
                    await music.ApplyNicknameAsync(bot);
            }

            var avatarResult = "";
            if (avatar is not null)
            {
                try
                {
                    using var http = new HttpClient();
                    var bytes = await http.GetByteArrayAsync(avatar.Url);
                    var format = avatar.ContentType switch { "image/png" => ImageFormat.Png, "image/gif" => ImageFormat.Gif, "image/webp" => ImageFormat.Webp, _ => ImageFormat.Jpeg };
                    await bot.Gateway.Rest.ModifyCurrentUserAsync(u => u.Avatar = new ImageProperties(format, bytes, false));
                    avatarResult = " Avatar changed.";
                }
                catch (RestException ex)
                {
                    avatarResult = $" Discord refused the avatar: {ex.Message}";
                }
            }

            var name = await music.DisplayNameAsync(bot);
            await ModifyResponseAsync(m => m.Content = $"Saved {name}.{avatarResult}");
        }

        [SubSlashCommand("phrases", "What a helper says at a moment: list, add, remove, or reset to its built-in lines")]
        public async Task<InteractionMessageProperties> PhrasesAsync(
            [SlashCommandParameter(Description = "Helper", AutocompleteProviderType = typeof(HelperAutocomplete))] string helper,
            [SlashCommandParameter(Description = "When it says it")] Moment moment,
            [SlashCommandParameter(Description = "What to do")] PhraseAction action = PhraseAction.List,
            [SlashCommandParameter(Description = "The phrase to add, or the number to remove; {track} {user} {channel} {helper} get filled in", MaxLength = 300)] string? text = null)
        {
            if (Find(helper) is not { } bot)
                return Replies.Ephemeral("There's no such helper.");

            var key = moment.ToString().ToLowerInvariant();
            var current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
            switch (action)
            {
                case PhraseAction.Add when !string.IsNullOrWhiteSpace(text):
                    current.Add(text.Trim());
                    await personalities.SaveAsync(bot.UserId, p => p.Phrases[key] = current);
                    break;
                case PhraseAction.Remove when int.TryParse(text, out var n) && n >= 1 && n <= current.Count:
                    current.RemoveAt(n - 1);
                    await personalities.SaveAsync(bot.UserId, p => p.Phrases[key] = current);
                    current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
                    break;
                case PhraseAction.Reset:
                    await personalities.SaveAsync(bot.UserId, p => p.Phrases.Remove(key));
                    current = (await personalities.PhrasesAsync(bot.UserId, bot.Index, key)).ToList();
                    break;
                case PhraseAction.Add or PhraseAction.Remove:
                    return Replies.Ephemeral(action == PhraseAction.Add ? "Give the phrase in `text`." : "Give the number to remove in `text`.");
            }

            return new()
            {
                Content = $"**{await music.DisplayNameAsync(bot)}**, {key}:\n" + string.Join('\n', current.Select((p, i) => $"{i + 1}. {p}")),
                Flags = MessageFlags.Ephemeral,
                AllowedMentions = AllowedMentionsProperties.None,
            };
        }

        private HelperBot? Find(string id) => music.Helpers.FirstOrDefault(h => h.UserId.ToString() == id);
    }
}
