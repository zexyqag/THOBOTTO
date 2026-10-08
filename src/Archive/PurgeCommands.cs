using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

using THOBOTTO.Access;

namespace THOBOTTO.Archive;

public sealed partial class ArchiveCommands
{
    [SubSlashCommand("purge", "Delete archived content for good (a message, a member's, or a channel's)")]
    [RequirePermission(BotPermissions.PurgeArchive)]
    public async Task<InteractionMessageProperties> PurgeAsync(
        [SlashCommandParameter(Description = "Why; kept in the audit log", MinLength = 3, MaxLength = 300)] string reason,
        [SlashCommandParameter(Description = "A message link or id")] string? message = null,
        [SlashCommandParameter(Description = "Everything this member wrote (in one channel, if given)")] User? member = null,
        [SlashCommandParameter(Description = "Everything in this channel (or this member's messages in it)")] Channel? channel = null)
    {
        PurgeScope scope;
        if (message is not null)
        {
            if (member is not null || channel is not null)
                return Replies.Ephemeral("Purge either one message, or a member and/or channel, not both.");
            if (MessageId().Match(message.Trim()) is not { Success: true } match)
                return Replies.Ephemeral("That isn't a message link or id.");
            scope = new(GuildId, ulong.Parse(match.Groups[1].Value), null, null);
        }
        else if (member is not null || channel is not null)
            scope = new(GuildId, null, member?.Id, channel?.Id);
        else
            return Replies.Ephemeral("Say what to purge: a message, a member, a channel, or a member in a channel.");

        var count = await purger.CountAsync(scope);
        if (count == 0)
            return Replies.Ephemeral($"There's nothing archived to purge for {scope.Describe()}.");

        if (scope.MessageId is not null)
        {
            await purger.PurgeAsync(scope, Context.User.Id, reason.Trim());
            return Replies.Ephemeral("Purged that message from the archive.");
        }

        var token = pending.Add(new(scope, Context.User.Id, reason.Trim()));
        return new()
        {
            Content = $"This purges **{count}** archived messages ({scope.Describe()}) for good: text, edits and attachments. The reason goes in the audit log.",
            Components = [new ActionRowProperties { new ButtonProperties($"archivepurge:{token}", $"Purge {count} messages", ButtonStyle.Danger) }],
            Flags = MessageFlags.Ephemeral,
            AllowedMentions = AllowedMentionsProperties.None,
        };
    }

    // A message link (…/channels/guild/channel/message) or a bare id.
    [GeneratedRegex(@"(?:channels/\d+/\d+/)?(\d{15,20})$")]
    private static partial Regex MessageId();
}

public sealed record PendingPurge(PurgeScope Scope, ulong ActorId, string Reason);

// Broad purges wait for a confirm button; this holds them for a few minutes.
public sealed class PendingPurges(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (PendingPurge Purge, DateTimeOffset Expires)> _pending = new();

    public string Add(PendingPurge purge)
    {
        foreach (var expired in _pending.Where(p => p.Value.Expires < time.GetUtcNow()).Select(p => p.Key).ToList())
            _pending.TryRemove(expired, out _);

        var token = Guid.NewGuid().ToString("N")[..16];
        _pending[token] = (purge, time.GetUtcNow() + Lifetime);
        return token;
    }

    public PendingPurge? Take(string token)
        => _pending.TryRemove(token, out var entry) && entry.Expires >= time.GetUtcNow() ? entry.Purge : null;
}

public sealed class PurgeConfirmButton(PendingPurges pending, Purger purger) : ComponentInteractionModule<ButtonInteractionContext>
{
    [ComponentInteraction("archivepurge")]
    public async Task ConfirmAsync(string token)
    {
        if (pending.Take(token) is not { } purge || purge.ActorId != Context.User.Id)
        {
            await RespondAsync(InteractionCallback.ModifyMessage(m =>
            {
                m.Content = "That confirmation has expired. Run the purge again.";
                m.Components = [];
            }));
            return;
        }

        await RespondAsync(InteractionCallback.DeferredModifyMessage);
        var count = await purger.PurgeAsync(purge.Scope, purge.ActorId, purge.Reason);
        await ModifyResponseAsync(m =>
        {
            m.Content = $"Purged {count} messages ({purge.Scope.Describe()}) from the archive.";
            m.Components = [];
        });
    }
}
