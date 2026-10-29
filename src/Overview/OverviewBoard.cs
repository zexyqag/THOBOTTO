using System.Collections.Concurrent;
using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Modules;

namespace THOBOTTO.Overview;

// The overview channels: widgets (one message each, in order) the bot keeps up to date. A widget is looked at again
// as often as its quickest block asks; its message is edited only when what it shows changed, and posted anew when
// it's gone or its look changed between cards and columns.
public sealed class OverviewBoard(IDbContextFactory<BotDbContext> dbFactory, IEnumerable<IWidgetKind> kinds, ModuleState modules, RestClient rest, TimeProvider time, ILogger<OverviewBoard> logger)
    : BackgroundService
{
    public const string ModuleId = "overview";
    public const int MaxWidgetsPerChannel = 20;
    public const int DefaultColour = 0x5865F2;

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
            return "There's no such block.";
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var inChannel = await db.OverviewWidgets.CountAsync(w => w.GuildId == guildId && w.ChannelId == channelId);
            if (inChannel >= MaxWidgetsPerChannel)
                return $"A channel holds at most {MaxWidgetsPerChannel} widgets.";
            db.OverviewWidgets.Add(new() { GuildId = guildId, ChannelId = channelId, Order = inChannel, Layout = WidgetLayout.Write([[new(kind.Id, [])]]), CreatedAt = time.GetUtcNow() });
            db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "overview.add", Details = $"{kind.Id} in {channelId}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            return $"Added, with {kind.Name}; it shows within a moment.";
        }
        finally
        {
            _gate.Release();
        }
    }

    // The editor's changes: title, colour, style and the layout.
    public async Task ChangeAsync(long id, Action<OverviewWidget> change)
    {
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return;
            var cards = WidgetStyles.Cards(widget.Style);
            change(widget);
            // Posted anew in the other look, and the channel's widgets below it after it, to keep their order.
            if (WidgetStyles.Cards(widget.Style) != cards)
                await ClearMessagesAsync(await db.OverviewWidgets.Where(w => w.GuildId == widget.GuildId && w.ChannelId == widget.ChannelId && w.Order >= widget.Order).ToListAsync());
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
            db.AuditEntries.Add(new() { GuildId = widget.GuildId, ActorId = actorId, Action = "overview.remove", Details = $"{widget.Title ?? "a widget"} in {widget.ChannelId}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            _looked.TryRemove(id, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    // As often as its quickest block asks.
    public TimeSpan RefreshOf(OverviewWidget widget)
        => WidgetLayout.Parse(widget.Layout).SelectMany(r => r).Select(c => Kind(c.Kind)?.Refresh).OfType<TimeSpan>().DefaultIfEmpty(TimeSpan.FromMinutes(5)).Min();

    // What a widget shows now: each cell's block, or what stands in for it.
    public async Task<WidgetMessage> RenderAsync(OverviewWidget widget)
    {
        List<IReadOnlyList<WidgetBlock>> rows = [];
        foreach (var row in WidgetLayout.Parse(widget.Layout))
        {
            List<WidgetBlock> blocks = [];
            foreach (var cell in row)
                blocks.Add(Kind(cell.Kind) is { } kind ? await kind.RenderAsync(widget.GuildId, WidgetOptions.Of(kind, cell.Options)) ?? new(kind.Name, "Nothing yet.") : new(cell.Kind, "A block that's gone."));
            rows.Add(blocks);
        }
        return WidgetMessage.Build(widget.Title, widget.Colour ?? DefaultColour, widget.Style, rows);
    }

    private async Task ClearMessagesAsync(IEnumerable<OverviewWidget> widgets)
    {
        foreach (var widget in widgets)
        {
            await DeleteMessageAsync(widget);
            _looked.TryRemove(widget.Id, out _);
        }
    }

    private async Task DeleteMessageAsync(OverviewWidget widget)
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
                if (!await modules.IsEnabledAsync(widget.GuildId, ModuleId))
                    continue;
                if (_looked.TryGetValue(widget.Id, out var looked) && now - looked < RefreshOf(widget) && widget.MessageId is not null)
                    continue;
                _looked[widget.Id] = now;
                await ShowAsync(widget, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ShowAsync(OverviewWidget widget, CancellationToken ct)
    {
        WidgetMessage message;
        try
        {
            message = await RenderAsync(widget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rendering widget {WidgetId} for {GuildId} failed", widget.Id, widget.GuildId);
            return;
        }
        if (widget.MessageId is not null && widget.Shown == message.Fingerprint)
            return;
        var flags = message.Cards ? MessageFlags.IsComponentsV2 : default;
        try
        {
            if (widget.MessageId is { } existing && WidgetMessage.LookOf(widget.Shown) == WidgetMessage.LookOf(message.Fingerprint))
            {
                try
                {
                    await rest.ModifyMessageAsync(widget.ChannelId, existing, m =>
                    {
                        (m.Components, m.Flags) = (message.Components, flags);
                        if (!message.Cards)
                            (m.Content, m.Embeds) = ("", [message.Embed!]);
                    }, cancellationToken: ct);
                    widget.Shown = message.Fingerprint;
                    return;
                }
                catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    widget.MessageId = null;
                }
            }
            await DeleteMessageAsync(widget);
            var posted = await rest.SendMessageAsync(widget.ChannelId, new()
            {
                Components = message.Components,
                Embeds = message.Embed is { } embed ? [embed] : [],
                AllowedMentions = AllowedMentionsProperties.None,
                Flags = MessageFlags.SuppressNotifications | flags,
            }, cancellationToken: ct);
            (widget.MessageId, widget.Shown) = (posted.Id, message.Fingerprint);
        }
        catch (RestException ex)
        {
            logger.LogWarning("Showing widget {WidgetId} in {ChannelId} of {GuildId} failed: {Message}", widget.Id, widget.ChannelId, widget.GuildId, ex.Message);
        }
    }
}
