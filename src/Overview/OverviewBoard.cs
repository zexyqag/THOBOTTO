using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Overview;

// The overview channels: widgets (one message each, in order) the bot keeps up to date. Each kind says how often it's
// looked at again; a message is edited only when what it shows changed, and posted anew when it's gone.
public sealed class OverviewBoard(IDbContextFactory<BotDbContext> dbFactory, IEnumerable<IWidgetKind> kinds, ModuleState modules, RestClient rest, TimeProvider time, ILogger<OverviewBoard> logger)
    : BackgroundService
{
    public const string ModuleId = "overview";
    public const int MaxWidgetsPerChannel = 20;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _looked = new();

    public IReadOnlyList<IWidgetKind> Kinds => [.. kinds.OrderBy(k => k.Name)];

    public IWidgetKind? Kind(string id) => kinds.FirstOrDefault(k => k.Id == id);

    public async Task<IReadOnlyList<OverviewWidget>> ListAsync(ulong guildId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.OverviewWidgets.AsNoTracking().Where(w => w.GuildId == guildId).OrderBy(w => w.ChannelId).ThenBy(w => w.Order).ToListAsync();
    }

    public async Task<string> AddAsync(ulong guildId, ulong channelId, string kindId, ulong actorId)
    {
        if (Kind(kindId) is not { } kind)
            return "There's no such widget.";
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var inChannel = await db.OverviewWidgets.CountAsync(w => w.GuildId == guildId && w.ChannelId == channelId);
            if (inChannel >= MaxWidgetsPerChannel)
                return $"A channel holds at most {MaxWidgetsPerChannel} widgets.";
            db.OverviewWidgets.Add(new() { GuildId = guildId, ChannelId = channelId, Kind = kind.Id, Order = inChannel, CreatedAt = time.GetUtcNow() });
            db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "overview.add", Details = $"{kind.Id} in {channelId}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            return $"{kind.Name} added; it shows within a moment.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetOptionsAsync(long id, IReadOnlyDictionary<string, string> options)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return;
            widget.Options = JsonSerializer.Serialize(options);
            await db.SaveChangesAsync();
            _looked.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Moves a widget one place up or down among its channel's; the channel's messages are posted afresh in the new order.
    public async Task MoveAsync(long id, int by)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return;
            var siblings = await db.OverviewWidgets.Where(w => w.GuildId == widget.GuildId && w.ChannelId == widget.ChannelId).OrderBy(w => w.Order).ToListAsync();
            var at = siblings.FindIndex(w => w.Id == id);
            var to = Math.Clamp(at + by, 0, siblings.Count - 1);
            if (to == at)
                return;
            siblings.RemoveAt(at);
            siblings.Insert(to, widget);
            for (var i = 0; i < siblings.Count; i++)
                siblings[i].Order = i;
            await ClearMessagesAsync(siblings);
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(long id, ulong actorId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return;
            await ClearMessagesAsync([widget]);
            db.OverviewWidgets.Remove(widget);
            db.AuditEntries.Add(new() { GuildId = widget.GuildId, ActorId = actorId, Action = "overview.remove", Details = $"{widget.Kind} in {widget.ChannelId}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            _looked.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ClearMessagesAsync(IEnumerable<OverviewWidget> widgets)
    {
        foreach (var widget in widgets)
        {
            if (widget.MessageId is { } messageId)
            {
                try
                {
                    await rest.DeleteMessageAsync(widget.ChannelId, messageId);
                }
                catch (RestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                }
            }
            (widget.MessageId, widget.Shown) = (null, null);
            _looked.TryRemove(widget.Id, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await KeepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Keeping the overview channels failed");
            }
        }
    }

    private async Task KeepAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = time.GetUtcNow();
            // In channel order, so a channel's messages come in the order of its widgets when posted afresh.
            foreach (var widget in await db.OverviewWidgets.OrderBy(w => w.GuildId).ThenBy(w => w.ChannelId).ThenBy(w => w.Order).ToListAsync(ct))
            {
                if (Kind(widget.Kind) is not { } kind || !await modules.IsEnabledAsync(widget.GuildId, ModuleId))
                    continue;
                if (_looked.TryGetValue(widget.Id, out var looked) && now - looked < kind.Refresh && widget.MessageId is not null)
                    continue;
                _looked[widget.Id] = now;
                await ShowAsync(widget, kind, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ShowAsync(OverviewWidget widget, IWidgetKind kind, CancellationToken ct)
    {
        WidgetView view;
        try
        {
            view = await kind.RenderAsync(widget.GuildId, WidgetOptions.Of(kind, widget.Options)) ?? new($"**{kind.Name}**: nothing yet.", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rendering the {Kind} widget for {GuildId} failed", kind.Id, widget.GuildId);
            return;
        }
        var shown = Fingerprint(view);
        if (widget.MessageId is { } messageId && widget.Shown == shown)
            return;
        try
        {
            if (widget.MessageId is { } existing)
            {
                try
                {
                    await rest.ModifyMessageAsync(widget.ChannelId, existing, m => (m.Content, m.Embeds) = (view.Content ?? "", view.Embed is { } e ? [e] : []), cancellationToken: ct);
                    widget.Shown = shown;
                    return;
                }
                catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    widget.MessageId = null;
                }
            }
            var posted = await rest.SendMessageAsync(widget.ChannelId, new()
            {
                Content = view.Content,
                Embeds = view.Embed is { } embed ? [embed] : [],
                AllowedMentions = AllowedMentionsProperties.None,
                Flags = NetCord.MessageFlags.SuppressNotifications,
            }, cancellationToken: ct);
            (widget.MessageId, widget.Shown) = (posted.Id, shown);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Showing the {Kind} widget in {ChannelId} of {GuildId} failed: {Message}", kind.Id, widget.ChannelId, widget.GuildId, ex.Message);
        }
    }

    private static string Fingerprint(WidgetView view)
    {
        var text = (view.Content ?? "") + "\n" + (view.Embed is { } e ? JsonSerializer.Serialize(new { e.Title, e.Description, e.Color, Fields = e.Fields?.Select(f => f.Name + f.Value) }) : "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
