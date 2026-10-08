using Microsoft.EntityFrameworkCore;

using NetCord.Gateway;

using THOBOTTO.Data;
using THOBOTTO.Modules;
using THOBOTTO.Voice;

namespace THOBOTTO.Points;

// Keeps every account in memory, advances them once a minute and on activity, and saves
// changed accounts each tick. All state changes happen under one lock; the gateway handlers
// only bump components, so they never wait on the database.
public sealed class PointsEngine(
    GatewayClient gateway,
    VoicePresence presence,
    IDbContextFactory<BotDbContext> dbFactory,
    ModuleState modules,
    SettingsStore settings,
    TimeProvider time,
    ILogger<PointsEngine> logger) : BackgroundService
{
    public const string ModuleId = "points";

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LedgerInterval = TimeSpan.FromHours(1);

    // Below this the member counts as idle and gets the idle floor.
    private const double IdleThreshold = 0.01;

    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<(ulong GuildId, ulong UserId), PointAccount> _accounts = [];
    private readonly HashSet<(ulong GuildId, ulong UserId)> _persisted = [];
    private readonly HashSet<(ulong GuildId, ulong UserId)> _dirty = [];
    private readonly Dictionary<ulong, PointRules> _rules = [];

    // Guilds where points run. Elsewhere accounts are paused: no pay, no decay, and no
    // back pay when they resume, since nothing was observed in between.
    private readonly HashSet<ulong> _active = [];
    private readonly List<PointEntry> _entries = [];

    // Anti-farming memory: reactions given per message, and reactions received per reactor.
    private readonly Dictionary<(ulong ReactorId, ulong MessageId), DateTimeOffset> _given = [];
    private readonly Dictionary<(ulong GuildId, ulong AuthorId, ulong ReactorId), List<DateTimeOffset>> _received = [];

    public PointRules Rules(ulong guildId)
    {
        lock (_sync)
            return _rules.GetValueOrDefault(guildId) ?? new();
    }

    public async Task SetRulesAsync(ulong guildId, PointRules rules, ulong actorId, string details)
    {
        await _loaded.Task;
        await settings.SetAsync(guildId, ModuleId, rules, actorId, details);

        lock (_sync)
        {
            // Settle everyone at the old rules first.
            AdvanceGuild(guildId, time.GetUtcNow());
            _rules[guildId] = rules;
        }
    }

    public async Task<PointStanding?> GetAsync(ulong guildId, ulong userId)
    {
        await _loaded.Task;
        lock (_sync)
        {
            if (!_accounts.TryGetValue((guildId, userId), out var account))
                return null;

            var rules = _rules.GetValueOrDefault(guildId) ?? new();
            Advance(account, rules, time.GetUtcNow());
            return Standing(account, rules);
        }
    }

    public async Task<IReadOnlyList<PointStanding>> TopAsync(ulong guildId, int count)
    {
        await _loaded.Task;
        lock (_sync)
        {
            var rules = _rules.GetValueOrDefault(guildId) ?? new();
            AdvanceGuild(guildId, time.GetUtcNow());
            return _accounts.Values
                .Where(a => a.GuildId == guildId)
                .OrderByDescending(a => a.Balance)
                .Take(count)
                .Select(a => Standing(a, rules))
                .ToList();
        }
    }

    // Returns the amount actually applied: without negative balances a deduction stops at zero.
    public async Task<double> AdjustAsync(ulong guildId, ulong userId, double amount, string reason, ulong actorId)
    {
        await _loaded.Task;
        var now = time.GetUtcNow();
        lock (_sync)
        {
            var rules = _rules.GetValueOrDefault(guildId) ?? new();
            var account = GetOrCreate(guildId, userId, now);
            Advance(account, rules, now);
            var before = account.Balance;
            account.Balance = Clamp(before + amount, before, rules);
            amount = account.Balance - before;
            _dirty.Add((guildId, userId));
            _entries.Add(new()
            {
                GuildId = guildId,
                UserId = userId,
                Amount = amount,
                Kind = PointEntryKinds.Adjust,
                Reason = reason,
                ActorId = actorId,
                CreatedAt = now,
            });
        }

        await SaveAsync(CancellationToken.None, audit: new()
        {
            GuildId = guildId,
            ActorId = actorId,
            Action = "points.adjust",
            Details = $"{userId} {amount:+0.##;-0.##} {reason}",
            CreatedAt = now,
        });
        return amount;
    }

    // Whether purchases cost points in this guild: only while the points module is on.
    public ValueTask<bool> ChargesAsync(ulong guildId) => modules.IsEnabledAsync(guildId, ModuleId);

    // Takes points for a purchase. False, and nothing taken, if the balance is too low.
    public async Task<bool> TrySpendAsync(ulong guildId, ulong userId, double amount, string reason)
    {
        await _loaded.Task;
        var now = time.GetUtcNow();
        lock (_sync)
        {
            var account = GetOrCreate(guildId, userId, now);
            Advance(account, _rules.GetValueOrDefault(guildId) ?? new(), now);
            if (account.Balance < amount)
                return false;

            account.Balance -= amount;
            _dirty.Add((guildId, userId));
            _entries.Add(new() { GuildId = guildId, UserId = userId, Amount = -amount, Kind = PointEntryKinds.Spend, Reason = reason, CreatedAt = now });
        }

        await SaveAsync(CancellationToken.None);
        return true;
    }

    // Moves points from one member to another in one step. False, and nothing moved, if the giver has too few.
    public async Task<bool> TransferAsync(ulong guildId, ulong fromId, ulong toId, double amount, string? reason)
    {
        await _loaded.Task;
        var now = time.GetUtcNow();
        lock (_sync)
        {
            var rules = _rules.GetValueOrDefault(guildId) ?? new();
            var from = GetOrCreate(guildId, fromId, now);
            var to = GetOrCreate(guildId, toId, now);
            Advance(from, rules, now);
            Advance(to, rules, now);
            if (from.Balance < amount)
                return false;

            from.Balance -= amount;
            to.Balance += amount;
            _dirty.Add((guildId, fromId));
            _dirty.Add((guildId, toId));
            _entries.Add(new() { GuildId = guildId, UserId = fromId, Amount = -amount, Kind = PointEntryKinds.Kudos, Reason = reason, ActorId = toId, CreatedAt = now });
            _entries.Add(new() { GuildId = guildId, UserId = toId, Amount = amount, Kind = PointEntryKinds.Kudos, Reason = reason, ActorId = fromId, CreatedAt = now });
        }

        await SaveAsync(CancellationToken.None);
        return true;
    }

    public Task RefundAsync(ulong guildId, ulong userId, double amount, string reason)
        => AwardAsync(guildId, userId, amount, PointEntryKinds.Refund, reason);

    // Gives points for something other than activity, e.g. a hall of fame bonus.
    public async Task AwardAsync(ulong guildId, ulong userId, double amount, string kind, string reason)
    {
        await _loaded.Task;
        var now = time.GetUtcNow();
        lock (_sync)
        {
            var account = GetOrCreate(guildId, userId, now);
            Advance(account, _rules.GetValueOrDefault(guildId) ?? new(), now);
            account.Balance += amount;
            _dirty.Add((guildId, userId));
            _entries.Add(new() { GuildId = guildId, UserId = userId, Amount = amount, Kind = kind, Reason = reason, CreatedAt = now });
        }

        await SaveAsync(CancellationToken.None);
    }

    public async Task OnMessageAsync(ulong guildId, ulong userId, int length)
    {
        await _loaded.Task;
        if (!await modules.IsEnabledAsync(guildId, ModuleId))
            return;

        var now = time.GetUtcNow();
        lock (_sync)
        {
            var rules = _rules.GetValueOrDefault(guildId) ?? new();
            var account = GetOrCreate(guildId, userId, now);
            if (account.LastChatAt is { } last && now - last < TimeSpan.FromSeconds(rules.ChatCooldownSeconds))
                return;

            Advance(account, rules, now);
            var bump = Math.Min(rules.ChatBumpMax, rules.ChatBump + length * rules.ChatBumpPerChar);
            account.Chat = Math.Min(rules.ChatMax, account.Chat + bump);
            account.LastChatAt = now;
        }
    }

    public async Task OnReactionAsync(ulong guildId, ulong reactorId, ulong authorId, ulong messageId)
    {
        await _loaded.Task;
        if (reactorId == authorId || !await modules.IsEnabledAsync(guildId, ModuleId))
            return;

        var now = time.GetUtcNow();
        lock (_sync)
        {
            var rules = _rules.GetValueOrDefault(guildId) ?? new();

            if (_given.TryAdd((reactorId, messageId), now))
            {
                var reactor = GetOrCreate(guildId, reactorId, now);
                Advance(reactor, rules, now);
                reactor.Given = Math.Min(rules.GivenMax, reactor.Given + rules.GivenBump);
            }

            var window = TimeSpan.FromMinutes(rules.ReceivedWindowMinutes);
            var recent = _received.TryGetValue((guildId, authorId, reactorId), out var list) ? list : _received[(guildId, authorId, reactorId)] = [];
            recent.RemoveAll(t => now - t >= window);
            if (recent.Count < rules.ReceivedPerReactor)
            {
                recent.Add(now);
                var author = GetOrCreate(guildId, authorId, now);
                Advance(author, rules, now);
                author.Received = Math.Min(rules.ReceivedMax, author.Received + rules.ReceivedBump);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync(stoppingToken);

        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Points tick failed");
            }
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.PointAccounts.AsNoTracking().ToListAsync(ct);
        var rules = await settings.GetAllAsync<PointRules>(ModuleId);

        lock (_sync)
        {
            foreach (var account in accounts)
            {
                _accounts[(account.GuildId, account.UserId)] = account;
                _persisted.Add((account.GuildId, account.UserId));
            }
            foreach (var (guildId, r) in rules)
                _rules[guildId] = r;
        }

        _loaded.SetResult();
        logger.LogInformation("Loaded {Count} point accounts", accounts.Count);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var guilds = gateway.Cache.Guilds.Keys.ToList();
        var enabled = new List<ulong>();
        foreach (var guildId in guilds)
            if (await modules.IsEnabledAsync(guildId, ModuleId))
                enabled.Add(guildId);

        lock (_sync)
        {
            foreach (var guildId in enabled.Where(g => !_active.Contains(g)))
                Resume(guildId, now);
            _active.Clear();
            _active.UnionWith(enabled);

            foreach (var guildId in enabled)
            {
                // Members earning in voice need an account even before their first message.
                foreach (var userId in QualifiedVoiceMembers(guildId))
                    GetOrCreate(guildId, userId, now);

                AdvanceGuild(guildId, now);
            }

            foreach (var key in _given.Where(g => now - g.Value > TimeSpan.FromDays(1)).Select(g => g.Key).ToList())
                _given.Remove(key);
            foreach (var key in _received.Where(r => r.Value.Count == 0 || now - r.Value[^1] > TimeSpan.FromDays(1)).Select(r => r.Key).ToList())
                _received.Remove(key);
        }

        await SaveAsync(ct);
    }

    private void Resume(ulong guildId, DateTimeOffset now)
    {
        foreach (var account in _accounts.Values.Where(a => a.GuildId == guildId))
        {
            account.UpdatedAt = now;
            _dirty.Add((account.GuildId, account.UserId));
        }
    }

    private void AdvanceGuild(ulong guildId, DateTimeOffset now)
    {
        var rules = _rules.GetValueOrDefault(guildId) ?? new();
        var inVoice = QualifiedVoiceMembers(guildId);
        foreach (var account in _accounts.Values.Where(a => a.GuildId == guildId))
            Advance(account, rules, inVoice, now);
    }

    private void Advance(PointAccount account, PointRules rules, DateTimeOffset now)
        => Advance(account, rules, QualifiedVoiceMembers(account.GuildId), now);

    // Pays out for the time since the last update at the activity level as it was, then
    // moves the components on and flushes an hour's earnings to the ledger.
    private void Advance(PointAccount account, PointRules rules, HashSet<ulong> inVoice, DateTimeOffset now)
    {
        var minutes = (now - account.UpdatedAt).TotalMinutes;
        if (minutes <= 0)
            return;

        if (!_active.Contains(account.GuildId))
        {
            account.UpdatedAt = now;
            _dirty.Add((account.GuildId, account.UserId));
            return;
        }

        var before = account.Balance;
        account.Balance = Clamp(before + rules.BasePerMinute * ActivityLevel(account, rules) * minutes, before, rules);
        account.PendingEarned += account.Balance - before;

        if (rules.WeeklyExpiryPercent > 0 && account.Balance > 0)
        {
            var kept = Math.Pow(1 - rules.WeeklyExpiryPercent / 100, minutes / TimeSpan.FromDays(7).TotalMinutes);
            var expired = account.Balance * (1 - kept);
            account.Balance -= expired;
            account.PendingExpired -= expired;
        }

        Evolve(account, rules, inVoice.Contains(account.UserId), minutes);
        account.UpdatedAt = now;
        account.PendingSince ??= now;

        if (now - account.PendingSince >= LedgerInterval)
            FlushPending(account, now);

        _dirty.Add((account.GuildId, account.UserId));
    }

    // Without negative balances, nothing may push a balance below zero, or lower than it already is.
    public static double Clamp(double balance, double before, PointRules rules)
        => rules.AllowNegativeBalance ? balance : Math.Max(balance, Math.Min(before, 0));

    public static double ActivityLevel(PointAccount account, PointRules rules)
    {
        var sum = account.Voice + account.Chat + account.Received + account.Given;
        return sum > IdleThreshold ? sum : rules.IdleFloor;
    }

    public static void Evolve(PointAccount account, PointRules rules, bool inVoice, double minutes)
    {
        var target = inVoice ? rules.VoiceMax : 0;
        var tau = target > account.Voice ? rules.VoiceRiseMinutes : rules.VoiceFallMinutes;
        account.Voice += (target - account.Voice) * (1 - Math.Exp(-minutes / tau));

        account.Chat *= HalfLife(minutes, rules.ChatHalfLifeMinutes);
        account.Received *= HalfLife(minutes, rules.ReceivedHalfLifeMinutes);
        account.Given *= HalfLife(minutes, rules.GivenHalfLifeMinutes);
    }

    private static double HalfLife(double minutes, double halfLife) => Math.Pow(0.5, minutes / halfLife);

    private void FlushPending(PointAccount account, DateTimeOffset now)
    {
        if (Math.Abs(account.PendingEarned) >= 0.005)
            _entries.Add(new() { GuildId = account.GuildId, UserId = account.UserId, Amount = account.PendingEarned, Kind = PointEntryKinds.Activity, CreatedAt = now });
        if (Math.Abs(account.PendingExpired) >= 0.005)
            _entries.Add(new() { GuildId = account.GuildId, UserId = account.UserId, Amount = account.PendingExpired, Kind = PointEntryKinds.Expired, CreatedAt = now });

        account.PendingEarned = 0;
        account.PendingExpired = 0;
        account.PendingSince = now;
    }

    private HashSet<ulong> QualifiedVoiceMembers(ulong guildId)
    {
        var afk = gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? guild.AfkChannelId : null;
        var humans = presence.Snapshot(guildId)
            .Where(p => !p.Value.IsBot && p.Value.ChannelId != afk)
            .ToList();

        // Earning needs another human in the channel; being deafened doesn't count as taking part.
        return humans
            .Where(p => !p.Value.Deafened && humans.Any(o => o.Key != p.Key && o.Value.ChannelId == p.Value.ChannelId))
            .Select(p => p.Key)
            .ToHashSet();
    }

    private PointAccount GetOrCreate(ulong guildId, ulong userId, DateTimeOffset now)
    {
        if (!_accounts.TryGetValue((guildId, userId), out var account))
        {
            _accounts[(guildId, userId)] = account = new() { GuildId = guildId, UserId = userId, UpdatedAt = now };
            _dirty.Add((guildId, userId));
        }
        return account;
    }

    private static PointStanding Standing(PointAccount a, PointRules rules) => new(
        a.UserId, a.Balance, ActivityLevel(a, rules), a.Voice, a.Chat, a.Received, a.Given, rules.BasePerMinute * ActivityLevel(a, rules));

    // One save at a time, so an account inserted by one save is never updated by another first.
    private async Task SaveAsync(CancellationToken ct, AuditEntry? audit = null)
    {
        await _saveGate.WaitAsync(ct);
        try
        {
            await SaveCoreAsync(ct, audit);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveCoreAsync(CancellationToken ct, AuditEntry? audit)
    {
        List<PointAccount> added, updated;
        List<PointEntry> entries;
        lock (_sync)
        {
            added = _dirty.Where(k => !_persisted.Contains(k)).Select(k => Copy(_accounts[k])).ToList();
            updated = _dirty.Where(k => _persisted.Contains(k)).Select(k => Copy(_accounts[k])).ToList();
            entries = [.. _entries];
            _dirty.Clear();
            _entries.Clear();
            foreach (var a in added)
                _persisted.Add((a.GuildId, a.UserId));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.PointAccounts.AddRange(added);
        db.PointAccounts.UpdateRange(updated);
        db.PointEntries.AddRange(entries);
        if (audit is not null)
            db.AuditEntries.Add(audit);
        await db.SaveChangesAsync(ct);
    }

    // Saved copies, so EF never tracks the live objects the lock protects.
    private static PointAccount Copy(PointAccount a) => new()
    {
        GuildId = a.GuildId,
        UserId = a.UserId,
        Balance = a.Balance,
        Voice = a.Voice,
        Chat = a.Chat,
        Received = a.Received,
        Given = a.Given,
        UpdatedAt = a.UpdatedAt,
        LastChatAt = a.LastChatAt,
        PendingEarned = a.PendingEarned,
        PendingExpired = a.PendingExpired,
        PendingSince = a.PendingSince,
    };
}

public sealed record PointStanding(
    ulong UserId, double Balance, double Level, double Voice, double Chat, double Received, double Given, double PerMinute);
