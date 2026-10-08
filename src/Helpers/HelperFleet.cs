using System.Collections.Concurrent;
using System.Net;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Music;

namespace THOBOTTO.Helpers;

public sealed class HelpersOptions
{
    public List<HelperOptions> Tokens { get; set; } = [];
}

public sealed class HelperOptions
{
    public string Token { get; set; } = "";
}

// What uses helpers (music, personalities): told about each one before it connects, and when it goes.
public interface IHelperAware
{
    Task AttachAsync(HelperBot helper);

    Task DetachAsync(HelperBot helper);
}

// The helper bots: those from configuration (Helpers:Tokens) and those added in the panel, whose
// tokens are kept encrypted in the database. Added or removed while the bot runs.
public sealed class HelperFleet(
    IOptions<HelpersOptions> options,
    IOptions<LavalinkOptions> lavalink,
    IDbContextFactory<BotDbContext> dbFactory,
    IDataProtectionProvider protection,
    IServiceProvider services,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private readonly ILogger _logger = loggers.CreateLogger<HelperFleet>();
    private readonly IDataProtector _protector = protection.CreateProtector("THOBOTTO.HelperTokens");
    private readonly ConcurrentDictionary<ulong, HelperBot> _helpers = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationToken _stopping;

    public IReadOnlyList<HelperBot> Helpers => _helpers.Values.OrderBy(h => h.UserId).ToList();

    // Configured helpers can't be removed in the panel; their tokens live in the deployment.
    public IReadOnlySet<ulong> Configured { get; private set; } = new HashSet<ulong>();

    // Checks the token with Discord, keeps it encrypted, and starts the helper. Returns a problem, if any.
    public async Task<string?> AddAsync(string token, ulong actorId)
    {
        token = token.Trim();
        CurrentUser user;
        try
        {
            using var check = new RestClient(new BotToken(token));
            user = await check.GetCurrentUserAsync();
        }
        catch (Exception ex) when (ex is RestException { StatusCode: HttpStatusCode.Unauthorized } or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return "Discord doesn't accept that token. Copy it again from the Bot tab (Reset Token shows a new one).";
        }
        if (!user.IsBot)
            return "That's not a bot's token.";
        if (services.GetRequiredService<NetCord.Gateway.GatewayClient>().Cache.User?.Id == user.Id)
            return "That's the main bot itself; a helper needs its own application.";

        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var account = await db.HelperAccounts.FindAsync(user.Id);
            if (account is null)
                db.HelperAccounts.Add(new() { UserId = user.Id, ProtectedToken = _protector.Protect(token), AddedById = actorId, AddedAt = time.GetUtcNow() });
            else
                account.ProtectedToken = _protector.Protect(token);
            db.AuditEntries.Add(new() { GuildId = 0, ActorId = actorId, Action = account is null ? "helpers.add" : "helpers.token", Details = $"{user.Username} {user.Id}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();

            // A replaced token means a fresh connection.
            await StopCoreAsync(user.Id);
            await StartCoreAsync(token);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(ulong helperId, ulong actorId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.HelperAccounts.Where(a => a.UserId == helperId).ExecuteDeleteAsync();
            db.AuditEntries.Add(new() { GuildId = 0, ActorId = actorId, Action = "helpers.remove", Details = helperId.ToString(), CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            await StopCoreAsync(helperId);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var configured = options.Value.Tokens.Select(t => t.Token.Trim()).Where(t => t.Length > 0).ToList();
        List<HelperAccount> stored;
        await using (var db = await dbFactory.CreateDbContextAsync(stoppingToken))
            stored = await db.HelperAccounts.ToListAsync(stoppingToken);

        await _gate.WaitAsync(stoppingToken);
        try
        {
            var ids = new HashSet<ulong>();
            foreach (var token in configured)
            {
                if (await StartCoreAsync(token) is { } id)
                    ids.Add(id);
            }
            Configured = ids;
            foreach (var account in stored.Where(a => !ids.Contains(a.UserId)))
            {
                try
                {
                    await StartCoreAsync(_protector.Unprotect(account.ProtectedToken));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    _logger.LogWarning("Helper {UserId}'s stored token can't be read (data protection keys changed); add it again", account.UserId);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        _logger.LogInformation("Started {Count} helpers", _helpers.Count);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var helper in _helpers.Values)
            await helper.DisposeAsync();
    }

    private async Task<ulong?> StartCoreAsync(string token)
    {
        HelperBot helper;
        try
        {
            helper = new HelperBot(token, lavalink.Value, loggers.CreateLogger<HelperBot>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("A helper token can't be used: {Message}", ex.Message);
            return null;
        }
        if (!_helpers.TryAdd(helper.UserId, helper))
            return helper.UserId;

        foreach (var aware in services.GetServices<IHelperAware>())
            await aware.AttachAsync(helper);
        await helper.Gateway.StartAsync(cancellationToken: _stopping);
        _ = helper.Lavalink.RunAsync(CancellationTokenSource.CreateLinkedTokenSource(_stopping, helper.Life.Token).Token);
        return helper.UserId;
    }

    private async Task StopCoreAsync(ulong helperId)
    {
        if (!_helpers.TryRemove(helperId, out var helper))
            return;
        foreach (var aware in services.GetServices<IHelperAware>())
        {
            try
            {
                await aware.DetachAsync(helper);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Detaching helper {Helper} failed", helper.Name);
            }
        }
        await helper.DisposeAsync();
    }
}
