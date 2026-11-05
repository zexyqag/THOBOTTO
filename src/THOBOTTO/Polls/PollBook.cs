using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.Rest;

using THOBOTTO.Access;
using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Happenings;
using THOBOTTO.Members;
using THOBOTTO.Modules;

namespace THOBOTTO.Polls;

// A poll as it's voted on: the stored one, with its votes kept here too, as the Activity reads it every moment.
public sealed class LivePoll(Poll poll, IEnumerable<PollVote> votes)
{
    public Poll Poll => poll;

    public ConcurrentDictionary<ulong, int> Votes { get; } = new(votes.Select(v => KeyValuePair.Create(v.UserId, v.Option)));

    // Changed since its message last showed it.
    public bool Dirty { get; set; }

    public DateTimeOffset ShownAt { get; set; }

    public int[] Counts()
    {
        var counts = new int[poll.Options.Length];
        foreach (var choice in Votes.Values)
            counts[choice]++;
        return counts;
    }
}

// Polls voted with buttons: asked with /poll in a channel, or in a voice channel's Activity for those in the call (one
// at a time there, posted in its chat as set). The message keeps the counts and becomes the result when it closes; the
// happenings forum gets the result too.
public sealed class PollBook(
    IDbContextFactory<BotDbContext> dbFactory,
    RestClient rest,
    HappeningForum forum,
    SettingsStore settings,
    AccessControl access,
    VoicePresence presence,
    TimeProvider time,
    ILogger<PollBook> logger) : BackgroundService
{
    public const string ModuleId = "polls";
    public static readonly HappeningKind Happening = new("poll", "Poll", "📊", "Poll results", TimeSpan.FromDays(3));
    public const int MostOptions = 6;
    // A closed poll stays in the Activity this long.
    private static readonly TimeSpan ShownAfter = TimeSpan.FromMinutes(10);
    // A poll's message shows votes at most this often (Discord limits edits).
    private static readonly TimeSpan ShowEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);

    private readonly ConcurrentDictionary<long, LivePoll> _live = new();
    private DateTimeOffset _swept;

    // The voice channel's Activity poll: open, or closed a little while ago.
    public LivePoll? In(ulong guildId, ulong channelId)
        => _live.Values.Where(p => p.Poll.InVoice && p.Poll.GuildId == guildId && p.Poll.ChannelId == channelId).MaxBy(p => p.Poll.Id);

    public LivePoll? Find(long pollId) => _live.GetValueOrDefault(pollId);

    // Opened, or why not.
    public async Task<(LivePoll? Poll, string? Problem)> CreateAsync(ulong guildId, ulong channelId, ulong askerId, string question, IReadOnlyList<string> options, TimeSpan open, bool inVoice)
    {
        var picked = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
        if (question.Trim().Length == 0 || picked.Count < 2)
            return (null, "A poll needs a question and at least two answers.");
        if (picked.Count > MostOptions || question.Length > 200 || picked.Any(o => o.Length > 80))
            return (null, $"At most {MostOptions} answers; the question up to 200 characters, each answer up to 80.");
        if (inVoice && In(guildId, channelId) is { Poll.Closed: false })
            return (null, "A poll is already open here.");
        var now = time.GetUtcNow();
        var poll = new Poll
        {
            GuildId = guildId,
            ChannelId = channelId,
            AskerId = askerId,
            Question = question.Trim(),
            Options = [.. picked],
            InVoice = inVoice,
            CreatedAt = now,
            ClosesAt = now + open,
        };
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Polls.Add(poll);
        await db.SaveChangesAsync();
        return (_live[poll.Id] = new LivePoll(poll, []), null);
    }

    // An Activity poll: opened, and posted in the voice channel's chat as set. What to say.
    public async Task<string> AskInVoiceAsync(ulong guildId, ulong channelId, ulong askerId, string question, IReadOnlyList<string> options, int minutes)
    {
        var (live, problem) = await CreateAsync(guildId, channelId, askerId, question, options, TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60)), inVoice: true);
        if (live is null)
            return problem!;
        if ((await settings.GetAsync<PollRules>(guildId, ModuleId)).InVoiceChat)
        {
            var (embed, components) = Card(live);
            try
            {
                var message = await rest.SendMessageAsync(channelId, new() { Embeds = [embed], Components = components, AllowedMentions = AllowedMentionsProperties.None });
                await SetMessageAsync(live, message.Id);
            }
            catch (RestException ex)
            {
                logger.LogWarning("Posting a poll in the chat of {ChannelId} of {GuildId} failed: {Message}", channelId, guildId, ex.Message);
            }
        }
        return "📊 Poll started.";
    }

    public async Task SetMessageAsync(LivePoll live, ulong messageId)
    {
        live.Poll.MessageId = messageId;
        live.ShownAt = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Polls.Where(p => p.Id == live.Poll.Id).ExecuteUpdateAsync(p => p.SetProperty(x => x.MessageId, messageId));
    }

    // Null once counted (again: a vote can change), else why not.
    public async Task<string?> VoteAsync(long pollId, ulong userId, int choice)
    {
        if (Find(pollId) is not { Poll.Closed: false } live || choice < 0 || choice >= live.Poll.Options.Length)
            return "That poll is closed.";
        if (live.Poll.InVoice && !(presence.Snapshot(live.Poll.GuildId).TryGetValue(userId, out var here) && here.ChannelId == live.Poll.ChannelId))
            return $"Only those in <#{live.Poll.ChannelId}> vote on this one.";
        live.Votes[userId] = choice;
        live.Dirty = true;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.PollVotes.Where(v => v.PollId == pollId && v.UserId == userId).ExecuteUpdateAsync(v => v.SetProperty(x => x.Option, choice)) == 0)
        {
            db.PollVotes.Add(new() { PollId = pollId, UserId = userId, Option = choice });
            await db.SaveChangesAsync();
        }
        return null;
    }

    // Closed early by who asked, or someone who manages polls; what to say.
    public async Task<string> CloseAsync(long pollId, Guild guild, GuildUser user)
    {
        if (Find(pollId) is not { Poll.Closed: false } live)
            return "That poll is closed already.";
        if (live.Poll.AskerId != user.Id && !await access.CanAsync(guild, user, BotPermissions.ManagePolls))
            return "Only who asked closes it early.";
        await EndAsync(live);
        return "📊 Poll closed.";
    }

    public static (EmbedProperties Embed, IEnumerable<IMessageComponentProperties> Components) Card(LivePoll live)
    {
        var poll = live.Poll;
        var counts = live.Counts();
        var votes = counts.Sum();
        var best = counts.Max();
        var lines = poll.Options.Select((o, i) =>
        {
            var share = votes == 0 ? 0 : counts[i] * 100 / votes;
            var mark = !poll.Closed ? Bar(share) : counts[i] == best && best > 0 ? "🏆" : "▫️";
            return $"{mark} **{o}** · {counts[i]} ({share}%)";
        });
        var when = poll.Closed ? $"closed <t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>" : $"closes <t:{poll.ClosesAt.ToUnixTimeSeconds()}:R>";
        var who = poll.InVoice ? $" · for those in <#{poll.ChannelId}>" : "";
        var embed = new EmbedProperties
        {
            Title = $"📊 {poll.Question}",
            Description = $"{string.Join('\n', lines)}\n-# Asked by <@{poll.AskerId}>{who} · {votes} {(votes == 1 ? "vote" : "votes")} · {when}",
            Color = new(0x5865F2),
        };
        if (poll.Closed)
            return (embed, []);
        var buttons = poll.Options.Select((o, i) => new ButtonProperties($"pollvote:{poll.Id}:{i}", Clip(o), ButtonStyle.Secondary)).ToList();
        List<IMessageComponentProperties> rows = [.. buttons.Chunk(5).Select(row => new ActionRowProperties(row))];
        rows.Add(new ActionRowProperties([new ButtonProperties($"pollclose:{poll.Id}", "Close it", ButtonStyle.Danger)]));
        return (embed, rows);
    }

    private static string Bar(int share)
    {
        var filled = (share + 5) / 10;
        return new string('▰', filled) + new string('▱', 10 - filled);
    }

    private static string Clip(string text) => text.Length <= 80 ? text : text[..79] + "…";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
        {
            var open = await db.Polls.Where(p => !p.Closed).ToListAsync(stoppingToken);
            var ids = open.Select(p => p.Id).ToList();
            var votes = await db.PollVotes.Where(v => ids.Contains(v.PollId)).ToListAsync(stoppingToken);
            foreach (var poll in open)
                _live[poll.Id] = new LivePoll(poll, votes.Where(v => v.PollId == poll.Id));
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = time.GetUtcNow();
                foreach (var live in _live.Values)
                {
                    if (!live.Poll.Closed && now >= live.Poll.ClosesAt)
                        await EndAsync(live);
                    else if (live.Poll.Closed && now - live.Poll.ClosesAt >= ShownAfter)
                        _live.TryRemove(live.Poll.Id, out _);
                    else if (live.Dirty && !live.Poll.Closed && now - live.ShownAt >= ShowEvery)
                        await ShowAsync(live);
                }
                if (now - _swept > TimeSpan.FromHours(1))
                    await SweepAsync(now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Keeping polls failed");
            }
        }
    }

    // The message as the poll stands.
    private async Task ShowAsync(LivePoll live)
    {
        live.Dirty = false;
        live.ShownAt = time.GetUtcNow();
        if (live.Poll.MessageId is not { } messageId)
            return;
        var (embed, components) = Card(live);
        try
        {
            await rest.ModifyMessageAsync(live.Poll.ChannelId, messageId, m => (m.Embeds, m.Components) = ([embed], components));
        }
        catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            logger.LogInformation("A poll's message in {ChannelId} of {GuildId} can't be changed: {Message}", live.Poll.ChannelId, live.Poll.GuildId, ex.Message);
            live.Poll.MessageId = null;
        }
    }

    private async Task EndAsync(LivePoll live)
    {
        lock (live)
        {
            if (live.Poll.Closed)
                return;
            live.Poll.Closed = true;
            live.Poll.ClosesAt = time.GetUtcNow();
        }
        await using (var db = await dbFactory.CreateDbContextAsync())
            await db.Polls.Where(p => p.Id == live.Poll.Id).ExecuteUpdateAsync(p => p.SetProperty(x => x.Closed, true).SetProperty(x => x.ClosesAt, live.Poll.ClosesAt));
        var (embed, _) = Card(live);
        if (live.Poll.MessageId is not null)
            await ShowAsync(live);
        // Not posted while open (off, or it couldn't be): the result is.
        if (live.Poll.MessageId is null && live.Poll.InVoice)
        {
            try
            {
                await rest.SendMessageAsync(live.Poll.ChannelId, new() { Embeds = [embed], AllowedMentions = AllowedMentionsProperties.None });
            }
            catch (RestException ex)
            {
                logger.LogWarning("Posting a poll's result in {ChannelId} of {GuildId} failed: {Message}", live.Poll.ChannelId, live.Poll.GuildId, ex.Message);
            }
        }
        await forum.ShowAsync(live.Poll.GuildId, Happening.Id, live.Poll.Id, HappeningStates.Open, new(live.Poll.Question, [embed], []));
    }

    private async Task SweepAsync(DateTimeOffset now)
    {
        _swept = now;
        await using var db = await dbFactory.CreateDbContextAsync();
        var old = await db.Polls.Where(p => p.Closed && p.ClosesAt < now - Kept).Select(p => p.Id).ToListAsync();
        if (old.Count == 0)
            return;
        await db.PollVotes.Where(v => old.Contains(v.PollId)).ExecuteDeleteAsync();
        await db.Polls.Where(p => old.Contains(p.Id)).ExecuteDeleteAsync();
    }
}

// The polls a member asked and how they voted, in a server.
public sealed class PollsData(IDbContextFactory<BotDbContext> dbFactory) : IMemberData
{
    public string Name => "Polls asked and voted on";

    public async Task<long> CountAsync(ulong guildId, ulong userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Polls.LongCountAsync(p => p.GuildId == guildId && p.AskerId == userId)
            + await db.PollVotes.LongCountAsync(v => v.UserId == userId && db.Polls.Any(p => p.Id == v.PollId && p.GuildId == guildId));
    }

    public async Task WriteAsync(ulong guildId, ulong userId, Utf8JsonWriter json)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var asked = await db.Polls.AsNoTracking().Where(p => p.GuildId == guildId && p.AskerId == userId).OrderBy(p => p.CreatedAt).ToListAsync();
        var voted = await db.PollVotes.AsNoTracking().Where(v => v.UserId == userId)
            .Join(db.Polls.Where(p => p.GuildId == guildId), v => v.PollId, p => p.Id, (v, p) => new { p.Question, p.Options, v.Option, p.CreatedAt })
            .OrderBy(x => x.CreatedAt).ToListAsync();
        json.WriteStartObject();
        json.WriteStartArray("asked");
        foreach (var poll in asked)
        {
            json.WriteStartObject();
            json.WriteString("question", poll.Question);
            json.WriteString("at", poll.CreatedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("voted");
        foreach (var vote in voted)
        {
            json.WriteStartObject();
            json.WriteString("question", vote.Question);
            json.WriteString("answer", vote.Options[vote.Option]);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}

public sealed class PollPostings(SettingsStore settings) : IPostingSource
{
    public async Task<IReadOnlyList<Posting>> PostingsAsync(Guild guild)
        => [
            new(PollBook.ModuleId, "Polls asked with /poll", new Landing.WhereStarted("where each is asked"), null),
            new(PollBook.ModuleId, "Activity polls", (await settings.GetAsync<PollRules>(guild.Id, PollBook.ModuleId)).InVoiceChat
                ? new Landing.WhereStarted("the voice channel's chat, as they open")
                : new Landing.WhereStarted("the voice channel's chat, their results"), Posting.Settings(guild.Id, PollBook.ModuleId)),
        ];
}
