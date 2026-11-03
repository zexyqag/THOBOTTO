using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;

namespace THOBOTTO.Discord;

// Posting under a member's name and avatar, through a webhook the bot keeps in the channel (a thread's in its parent),
// so what they post with a command reads as theirs. Only in a server with the bot, where it may make webhooks and the
// member may write; elsewhere the command replies as the bot.
public sealed partial class AsMember(RestClient rest, ILogger<AsMember> logger)
{
    private const string HookName = "Posts as members";

    private readonly ConcurrentDictionary<ulong, IncomingWebhook> _hooks = new();
    private readonly SemaphoreSlim _making = new(1, 1);

    public static bool Can(Interaction interaction)
        => interaction is { Guild: not null, User: GuildInteractionUser member, Channel: TextGuildChannel channel }
            && interaction.AppPermissions.HasFlag(Permissions.ManageWebhooks)
            && member.Permissions.HasFlag(channel is GuildThread ? Permissions.SendMessagesInThreads : Permissions.SendMessages);

    // Null once posted, else why not.
    public async Task<string?> PostAsync(Interaction interaction, string content)
    {
        var member = (GuildInteractionUser)interaction.User;
        var (channelId, threadId) = interaction.Channel is GuildThread thread ? (thread.ParentId, (ulong?)thread.Id) : (interaction.Channel.Id, null);
        var message = new WebhookMessageProperties
        {
            Content = content,
            Username = WebhookName(member.Nickname ?? member.GlobalName ?? member.Username),
            AvatarUrl = (member.GetGuildAvatarUrl() ?? member.GetAvatarUrl() ?? member.DefaultAvatarUrl).ToString(),
            AllowedMentions = AllowedMentionsProperties.None,
        };
        try
        {
            try
            {
                await ExecuteAsync(await HookAsync(channelId, interaction.ApplicationId), message, threadId);
            }
            catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Its webhook was deleted: made again.
                _hooks.TryRemove(channelId, out _);
                await ExecuteAsync(await HookAsync(channelId, interaction.ApplicationId), message, threadId);
            }
            return null;
        }
        catch (RestException ex)
        {
            logger.LogWarning("Couldn't post as {User} in {Channel}: {Message}", member.Id, channelId, ex.Message);
            return ex.Message;
        }
    }

    private Task ExecuteAsync(IncomingWebhook hook, WebhookMessageProperties message, ulong? threadId)
        => rest.ExecuteWebhookAsync(hook.Id, hook.Token, message, threadId: threadId);

    private async Task<IncomingWebhook> HookAsync(ulong channelId, ulong applicationId)
    {
        if (_hooks.TryGetValue(channelId, out var known))
            return known;
        await _making.WaitAsync();
        try
        {
            if (_hooks.TryGetValue(channelId, out known))
                return known;
            var hook = (await rest.GetChannelWebhooksAsync(channelId)).OfType<IncomingWebhook>()
                .FirstOrDefault(h => h.ApplicationId == applicationId && h.Name == HookName && h.Token is not null)
                ?? await rest.CreateWebhookAsync(channelId, new WebhookProperties(HookName));
            return _hooks[channelId] = hook;
        }
        finally
        {
            _making.Release();
        }
    }

    // Discord refuses webhook names with "discord" or "clyde" in them, and longer than 80.
    public static string WebhookName(string name)
    {
        var named = Refused().Replace(name, m => $"{m.Value[0]}​{m.Value[1..]}");
        return named.Length <= 80 ? named : named[..80];
    }

    [GeneratedRegex("discord|clyde", RegexOptions.IgnoreCase)]
    private static partial Regex Refused();
}
