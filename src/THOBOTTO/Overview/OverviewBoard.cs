using System.Collections.Concurrent;
using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Rest;

using THOBOTTO.Data;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Overview;

// The overview channels: widgets (one message each, in order) the bot keeps up to date. A widget is looked at again
// as often as its quickest block asks, and at once when it's changed on the panel; its message is edited only when
// what it shows changed, and posted anew when it's gone or its look changed between cards and columns.
public sealed class OverviewBoard(IDbContextFactory<BotDbContext> dbFactory, IEnumerable<IWidgetKind> kinds, ModuleState modules, RestClient rest, TimeProvider time, ILogger<OverviewBoard> logger)
    : BackgroundService
{
    public const string ModuleId = "overview";
    public const int MaxWidgetsPerChannel = 20;
    public const int DefaultColour = 0x5865F2;

    // What a widget whose blocks all hid themselves "shows": no message.
    public const string Hidden = "hidden";

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

    // Whether any of the server's widgets holds that kind of block.
    public async Task<bool> ShowsAsync(ulong guildId, string kindId)
        => (await ListAsync(guildId)).Any(w => WidgetLayout.Parse(w.Layout).Any(r => r.Any(c => c.Kind == kindId)));

    // What to say, and whether it went wrong.
    public async Task<(string Message, bool Failed)> AddAsync(ulong guildId, ulong channelId, string kindId, ulong actorId)
    {
        if (Kind(kindId) is not { } kind)
            return ("There's no such block.", true);
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var inChannel = await db.OverviewWidgets.CountAsync(w => w.GuildId == guildId && w.ChannelId == channelId);
            if (inChannel >= MaxWidgetsPerChannel)
                return ($"A channel holds at most {MaxWidgetsPerChannel} widgets.", true);
            db.OverviewWidgets.Add(new() { GuildId = guildId, ChannelId = channelId, Order = inChannel, Layout = WidgetLayout.Write([[new(kind.Id, [])]]), CreatedAt = time.GetUtcNow() });
            db.AuditEntries.Add(new() { GuildId = guildId, ActorId = actorId, Action = "overview.add", Details = $"{kind.Id} in {channelId}", CreatedAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        finally
        {
            _gate.Release();
        }
        return await KeepAsync((guildId, channelId), CancellationToken.None) is { } problem
            ? ($"Added, with {kind.Name}, but posting it failed: {problem}", true)
            : ($"Added, with {kind.Name}.", false);
    }

    // The editor's changes: title, colour, style and the layout. Posted at once; why that failed, if it did.
    public async Task<string?> ChangeAsync(long id, Action<OverviewWidget> change)
    {
        (ulong, ulong) channel;
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return null;
            channel = (widget.GuildId, widget.ChannelId);
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
        return await KeepAsync(channel, CancellationToken.None);
    }

    // Moves a widget one place up or down among its channel's; the channel's messages are posted afresh in the new
    // order, at once. Why that failed, if it did.
    public async Task<string?> MoveAsync(long id, int by)
    {
        (ulong, ulong) channel;
        await _gate.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (await db.OverviewWidgets.FindAsync(id) is not { } widget)
                return null;
            channel = (widget.GuildId, widget.ChannelId);
            var siblings = await db.OverviewWidgets.Where(w => w.GuildId == widget.GuildId && w.ChannelId == widget.ChannelId).OrderBy(w => w.Order).ToListAsync();
            var at = siblings.FindIndex(w => w.Id == id);
            var to = Math.Clamp(at + by, 0, siblings.Count - 1);
            if (to == at)
                return null;
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
        return await KeepAsync(channel, CancellationToken.None);
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

    // What a widget shows now: each cell's block, or what stands in for it; null when every block hid itself.
    public async Task<WidgetMessage?> RenderAsync(OverviewWidget widget)
    {
        List<IReadOnlyList<RenderedCell>> rows = [];
        foreach (var row in WidgetLayout.Parse(widget.Layout))
        {
            List<RenderedCell> cells = [];
            foreach (var cell in row)
            {
                if (Kind(cell.Kind) is { } kind)
                {
                    var options = WidgetOptions.Of(kind, cell.Options);
                    cells.Add(new(kind.Name, await kind.RenderAsync(widget.GuildId, options), WidgetOptions.Int(options, WidgetOptions.Columns, 1, 1, WidgetLayout.MostCells), cell.HideEmpty));
                }
                else
                    cells.Add(new(cell.Kind, [new(cell.Kind, "A block that's gone.")], 1));
            }
            rows.Add(cells);
        }
        var arranged = WidgetLayout.Arrange(rows);
        return arranged.Count == 0 && rows.Count > 0 ? null : WidgetMessage.Build(widget.Title, widget.Colour ?? DefaultColour, widget.Style, arranged);
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
                await KeepAsync(null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Keeping the overview channels failed");
            }
        }
    }

    // The widgets that are due, every channel's or one's; the first thing that went wrong.
    private async Task<string?> KeepAsync((ulong GuildId, ulong ChannelId)? only, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var now = time.GetUtcNow();
            var widgets = db.OverviewWidgets.AsQueryable();
            if (only is var (guildId, channelId))
                widgets = widgets.Where(w => w.GuildId == guildId && w.ChannelId == channelId);
            string? problem = null;
            // In channel order, so a channel's messages come in the order of its widgets when posted afresh.
            var all = await widgets.OrderBy(w => w.GuildId).ThenBy(w => w.ChannelId).ThenBy(w => w.Order).ToListAsync(ct);
            foreach (var widget in all)
            {
                if (!await modules.IsEnabledAsync(widget.GuildId, ModuleId))
                    continue;
                if (_looked.TryGetValue(widget.Id, out var looked) && now - looked < RefreshOf(widget) && (widget.MessageId is not null || widget.Shown == Hidden))
                    continue;
                _looked[widget.Id] = now;
                var had = widget.MessageId;
                problem ??= await ShowAsync(widget, ct);
                // One posted anew (back from hiding, or its message deleted) goes below the channel's later widgets: they
                // follow it again.
                if (had is null && widget.MessageId is not null)
                    await ClearMessagesAsync(all.Where(w => w.GuildId == widget.GuildId && w.ChannelId == widget.ChannelId && w.Order > widget.Order && w.MessageId is not null).ToList());
            }
            await db.SaveChangesAsync(ct);
            return problem;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Why it couldn't be shown, or null.
    private async Task<string?> ShowAsync(OverviewWidget widget, CancellationToken ct)
    {
        WidgetMessage? rendered;
        try
        {
            rendered = await RenderAsync(widget);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rendering widget {WidgetId} for {GuildId} failed", widget.Id, widget.GuildId);
            return "a block failed to put itself together (the bot's log says why).";
        }
        if (rendered is not { } message)
        {
            await DeleteMessageAsync(widget);
            widget.Shown = Hidden;
            return null;
        }
        if (widget.MessageId is not null && widget.Shown == message.Fingerprint)
            return null;
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
                    return null;
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
            return null;
        }
        catch (RestException ex)
        {
            var said = DiscordErrors.Describe(ex);
            logger.LogWarning("Showing widget {WidgetId} in {ChannelId} of {GuildId} failed: {Message}", widget.Id, widget.ChannelId, widget.GuildId, said);
            return $"Discord said \"{said}\".";
        }
    }
}
