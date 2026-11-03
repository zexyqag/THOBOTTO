using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Moderation;
using THOBOTTO.Modules;
using THOBOTTO.Reposts;

namespace THOBOTTO.Anonymous;

public static class AnonymousNaming
{
    public const string Numbered = "numbered";
    public const string Plain = "plain";
}

// Stored with SettingsStore under the module id.
public sealed record AnonymousRules
{
    [Setting("Anonymous channels", Help = "Every message there is posted again under no one's name, and theirs removed. One written there shows under its writer's name for a moment first; /anonymous posts there without that.", Kind = SettingKind.TextChannel)]
    public IReadOnlyList<ulong> ChannelIds { get; init; } = [];

    [Setting("Named", Choices = [AnonymousNaming.Numbered + "=Anonymous 3: the same number for a member all day, so a conversation can be followed", AnonymousNaming.Plain + "=Anonymous, for everyone"])]
    public string Naming { get; init; } = AnonymousNaming.Numbered;

    [Setting("Moderators can see who wrote one", Help = "Right-click → Apps → Who wrote this?, for those who can warn; each look is noted in the moderation log. Off: nobody can (the archive, for those who may read it, still keeps the messages as written).")]
    public bool ModeratorsSee { get; init; } = true;
}

// The anonymous channels of every server, kept in memory so each message is looked at cheaply, and the names posts
// there go under.
public sealed class AnonymousChannels(SettingsStore settings, TimeProvider time) : IHostedService
{
    public const string ModuleId = "anonymous";

    private readonly ConcurrentDictionary<ulong, IReadOnlyList<ulong>> _byGuild = new();
    private volatile HashSet<ulong> _all = [];
    // (server, day) → member → their number that day.
    private readonly ConcurrentDictionary<(ulong, DateOnly), ConcurrentDictionary<ulong, int>> _numbers = new();

    public bool Is(ulong channelId) => _all.Contains(channelId);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (guildId, rules) in await settings.GetAllAsync<AnonymousRules>(ModuleId))
            _byGuild[guildId] = rules.ChannelIds;
        Refresh();
        settings.Changed += async (guildId, module) =>
        {
            if (module != ModuleId)
                return;
            _byGuild[guildId] = (await settings.GetAsync<AnonymousRules>(guildId, ModuleId)).ChannelIds;
            Refresh();
        };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Refresh() => _all = [.. _byGuild.Values.SelectMany(c => c)];

    // The name and picture a post goes under: one of Discord's plain avatars, by the number.
    public (string Name, string AvatarUrl) Of(ulong guildId, ulong userId, AnonymousRules rules)
    {
        if (rules.Naming == AnonymousNaming.Plain)
            return ("Anonymous", "https://cdn.discordapp.com/embed/avatars/0.png");
        var today = _numbers.GetOrAdd((guildId, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime)), _ => new());
        var number = today.GetOrAdd(userId, _ => today.Count + 1);
        return ($"Anonymous {number}", $"https://cdn.discordapp.com/embed/avatars/{number % 6}.png");
    }
}

// A message in an anonymous channel, posted again under no one's name.
public sealed class AnonymousVoice(AnonymousChannels channels, SettingsStore settings, ModuleState modules) : IRewrite
{
    public RewriteStage Stage => RewriteStage.Who;

    public bool Wants(string text, ulong channelId, ulong userId) => channels.Is(channelId);

    public async Task RewriteAsync(Repost repost, PostingPreference preference)
    {
        var guildId = repost.Guild.Id;
        if (!await modules.IsEnabledAsync(guildId, AnonymousChannels.ModuleId))
            return;
        var rules = await settings.GetAsync<AnonymousRules>(guildId, AnonymousChannels.ModuleId);
        if (!rules.ChannelIds.Contains(repost.Channel.Id))
            return;
        (repost.Name, repost.AvatarUrl) = channels.Of(guildId, repost.Member.Id, rules);
        (repost.Anonymous, repost.Changed) = (true, true);
    }
}

// /anonymous: posted in an anonymous channel without the writer's name ever showing.
public sealed class AnonymousCommand(AnonymousChannels channels, AsMember asMember, SettingsStore settings, ModuleState modules, Reposter reposter, [FromKeyedServices(nameof(AnonymousChannels))] HttpClient http) : GuildCommandModule
{
    [SlashCommand("anonymous", "Post here under no one's name (in an anonymous channel)", Contexts = [InteractionContextType.Guild])]
    public async Task PostAsync(
        [SlashCommandParameter(Description = "What to say", MaxLength = 2000)] string text,
        [SlashCommandParameter(Description = "A file with it")] Attachment? file = null)
    {
        var rules = await settings.GetAsync<AnonymousRules>(GuildId, AnonymousChannels.ModuleId);
        if (!await modules.IsEnabledAsync(GuildId, AnonymousChannels.ModuleId) || !await modules.IsEnabledAsync(GuildId, Reposter.ModuleId)
            || Context.Channel is not TextGuildChannel channel || !rules.ChannelIds.Contains(channel.Id))
        {
            await RespondAsync(InteractionCallback.Message(Replies.Ephemeral("This isn't an anonymous channel.")));
            return;
        }
        await RespondAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral));
        var member = (GuildUser)Context.User;
        var (name, avatar) = channels.Of(GuildId, member.Id, rules);
        using var stream = file is null ? null : new MemoryStream(await http.GetByteArrayAsync(file.Url));
        var (_, problem) = await asMember.PostForAsync(channel, GuildId, member.Id, new()
        {
            Content = await reposter.RewriteAsync(Guild, member, channel, text.Trim()),
            Username = name,
            AvatarUrl = avatar,
            Attachments = file is null ? null : [new AttachmentProperties(file.FileName, stream!)],
        }, anonymous: true);
        await ModifyResponseAsync(m => m.Content = problem is null ? $"Posted as {name}. Right-click it → Apps → Edit my post to change or remove it." : $"It couldn't be posted ({problem}).");
    }
}

// Right-click an anonymous post, Apps, Who wrote this?: for moderators, where the server allows it, noted in the
// moderation log each time.
public sealed class WhoWroteCommand(AsMember asMember, SettingsStore settings, RestClient rest, IDbContextFactory<BotDbContext> dbFactory, TimeProvider time) : GuildCommandModule
{
    [MessageCommand("Who wrote this?", Contexts = [InteractionContextType.Guild], DefaultGuildPermissions = Permissions.ModerateUsers)]
    [RequirePermission(BotPermissions.ModWarn)]
    public async Task<InteractionMessageProperties> WhoAsync(RestMessage message)
    {
        if (message.WebhookId is null || await asMember.FindAsync(message.Id) is not { Anonymous: true } post)
            return Replies.Ephemeral("That isn't an anonymous post.");
        if (!(await settings.GetAsync<AnonymousRules>(GuildId, AnonymousChannels.ModuleId)).ModeratorsSee)
            return Replies.Ephemeral("This server keeps anonymous posts anonymous, from moderators too.");
        var link = Notifications.Notifier.Link(GuildId, message.ChannelId, message.Id);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.AuditEntries.Add(new() { GuildId = GuildId, ActorId = Context.User.Id, Action = "anonymous.reveal", Details = link, CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        if ((await settings.GetAsync<ModRules>(GuildId, CaseBook.ModuleId)).LogChannelId is { } log)
        {
            try
            {
                await rest.SendMessageAsync(log, new() { Content = $"🔍 <@{Context.User.Id}> looked up who wrote an anonymous post: {link}", AllowedMentions = AllowedMentionsProperties.None });
            }
            catch (RestException)
            {
            }
        }
        return Replies.Ephemeral($"<@{post.UserId}> wrote it (this look is noted in the moderation log).");
    }
}
