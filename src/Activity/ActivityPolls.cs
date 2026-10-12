using System.Collections.Concurrent;

using NetCord.Rest;

namespace THOBOTTO.Activity;

public sealed class ActivityPoll(ulong guildId, ulong channelId, ulong createdBy, string question, IReadOnlyList<string> options, DateTimeOffset closesAt)
{
    public ulong GuildId => guildId;

    public ulong ChannelId => channelId;

    public ulong CreatedBy => createdBy;

    public string Question => question;

    public IReadOnlyList<string> Options => options;

    public DateTimeOffset ClosesAt { get; set; } = closesAt;

    public bool Closed { get; set; }

    public ConcurrentDictionary<ulong, int> Votes { get; } = new();

    public int[] Counts()
    {
        var counts = new int[options.Count];
        foreach (var choice in Votes.Values)
            counts[choice]++;
        return counts;
    }
}

// Quick polls in a voice channel's Activity, one at a time per channel, voted by those in it; kept in memory, the
// result posted in the channel's chat when it closes and shown a while after.
public sealed class ActivityPolls(RestClient rest, TimeProvider time, ILogger<ActivityPolls> logger) : BackgroundService
{
    public const int MostOptions = 6;
    private static readonly TimeSpan ShownAfter = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<(ulong, ulong), ActivityPoll> _polls = new();

    public ActivityPoll? In(ulong guildId, ulong channelId) => _polls.GetValueOrDefault((guildId, channelId));

    public string Create(ulong guildId, ulong channelId, ulong userId, string question, IReadOnlyList<string> options, int minutes)
    {
        var picked = options.Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
        if (question.Trim().Length == 0 || picked.Count < 2)
            return "A poll needs a question and at least two answers.";
        if (picked.Count > MostOptions || question.Length > 200 || picked.Any(o => o.Length > 80))
            return $"At most {MostOptions} answers; the question up to 200 characters, each answer up to 80.";
        if (In(guildId, channelId) is { Closed: false })
            return "A poll is already open here.";
        _polls[(guildId, channelId)] = new(guildId, channelId, userId, question.Trim(), picked, time.GetUtcNow() + TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60)));
        return "📊 Poll started.";
    }

    // Voting again changes the vote.
    public string? Vote(ulong guildId, ulong channelId, ulong userId, int choice)
    {
        if (In(guildId, channelId) is not { Closed: false } poll || choice < 0 || choice >= poll.Options.Count)
            return "That poll is closed.";
        poll.Votes[userId] = choice;
        return null;
    }

    public async Task<string> CloseAsync(ulong guildId, ulong channelId, ulong userId)
    {
        if (In(guildId, channelId) is not { Closed: false } poll)
            return "No poll is open here.";
        if (poll.CreatedBy != userId)
            return "Only who asked closes the poll early.";
        await EndAsync(poll);
        return "📊 Poll closed.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = time.GetUtcNow();
                foreach (var poll in _polls.Values)
                    if (!poll.Closed && now >= poll.ClosesAt)
                        await EndAsync(poll);
                    else if (poll.Closed && now - poll.ClosesAt >= ShownAfter)
                        _polls.TryRemove(new((poll.GuildId, poll.ChannelId), poll));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Closing polls failed");
            }
        }
    }

    private async Task EndAsync(ActivityPoll poll)
    {
        lock (poll)
        {
            if (poll.Closed)
                return;
            poll.Closed = true;
            poll.ClosesAt = time.GetUtcNow();
        }
        var counts = poll.Counts();
        var total = Math.Max(1, counts.Sum());
        var best = counts.Max();
        var lines = poll.Options.Select((o, i) => $"{(counts[i] == best && best > 0 ? "🏆" : "▫️")} **{o}** · {counts[i]} ({counts[i] * 100 / total}%)");
        try
        {
            await rest.SendMessageAsync(poll.ChannelId, new()
            {
                Content = $"📊 <@{poll.CreatedBy}> asked: **{poll.Question}**\n{string.Join('\n', lines)}",
                AllowedMentions = AllowedMentionsProperties.None,
            });
        }
        catch (RestException ex)
        {
            logger.LogDebug("Posting a poll's result in {ChannelId} of {GuildId}: {Message}", poll.ChannelId, poll.GuildId, ex.Message);
        }
    }
}
