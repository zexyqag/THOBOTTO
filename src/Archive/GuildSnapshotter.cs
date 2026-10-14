using System.Net;

using Microsoft.EntityFrameworkCore;

using NetCord;
using NetCord.Gateway;
using NetCord.JsonModels;
using NetCord.Rest;
using NetCord.Rest.JsonModels;

using THOBOTTO.Data;
using THOBOTTO.Debugging;
using THOBOTTO.Discord;
using THOBOTTO.Modules;

namespace THOBOTTO.Archive;

public enum SnapshotPart
{
    Guild,
    Channels,
    Roles,
    Members,
    Emojis,
    Stickers,
    Bans,
    Webhooks,
    Events,
    Pins,
}

// Takes the server itself into the archive: what it is, its channels and threads, roles, members, emojis,
// stickers, bans, webhooks, scheduled events and pins, and their images through the attachment store. Whole
// at a start, once a day and on `/archive snapshot`; a part at a time when Discord says that part changed.
// The tables are written through the Archiver's queue, in order with the live events.
public sealed class GuildSnapshotter(
    RestClient rest,
    GatewayClient gateway,
    Archiver archiver,
    IAttachmentStore store,
    [FromKeyedServices(nameof(Archiver))] HttpClient http,
    DiscordApp app,
    ModuleState modules,
    IDbContextFactory<BotDbContext> dbFactory,
    TimeProvider time,
    ILogger<GuildSnapshotter> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    // A start soon after a whole snapshot (a restart, a reconnect) doesn't take another.
    private static readonly TimeSpan Recent = TimeSpan.FromHours(1);

    // Pins are asked for channel by channel, and Discord limits that endpoint tightly.
    private static readonly TimeSpan PinsPace = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);
    private const long MaxAssetBytes = 25 * 1024 * 1024;
    private const int PageSize = 1000;

    private readonly Dictionary<ulong, Wanted> _requests = [];
    private readonly SemaphoreSlim _wake = new(0, 1);

    private sealed class Wanted
    {
        public HashSet<SnapshotPart> Parts { get; } = [];

        public HashSet<ulong> PinChannels { get; } = [];

        public bool Whole { get; set; }

        public bool Forced { get; set; }
    }

    private sealed record Asset(string Kind, ulong EntityId, string Hash, string Name, string Url);

    // The whole server; unless forced, not again within an hour of the last time.
    public void RequestWhole(ulong guildId, bool force)
    {
        lock (_requests)
        {
            var request = For(guildId);
            request.Whole = true;
            request.Forced |= force;
        }
        Wake();
    }

    public void Request(ulong guildId, SnapshotPart part)
    {
        lock (_requests)
            For(guildId).Parts.Add(part);
        Wake();
    }

    public void RequestPins(ulong guildId, ulong channelId)
    {
        lock (_requests)
        {
            var request = For(guildId);
            request.Parts.Add(SnapshotPart.Pins);
            request.PinChannels.Add(channelId);
        }
        Wake();
    }

    private Wanted For(ulong guildId) => _requests.TryGetValue(guildId, out var wanted) ? wanted : _requests[guildId] = new();

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextDaily = time.GetUtcNow() + Every;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
                if (time.GetUtcNow() >= nextDaily)
                {
                    foreach (var guildId in gateway.Cache.Guilds.Keys.ToList())
                        RequestWhole(guildId, force: true);
                    nextDaily += Every;
                }

                List<KeyValuePair<ulong, Wanted>> requests;
                lock (_requests)
                {
                    requests = [.. _requests];
                    _requests.Clear();
                }
                foreach (var (guildId, request) in requests)
                {
                    try
                    {
                        await TakeAsync(guildId, request, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Taking the snapshot of {GuildId} failed", guildId);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Taking server snapshots failed; trying again shortly");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task TakeAsync(ulong guildId, Wanted request, CancellationToken ct)
    {
        if (!await modules.IsEnabledAsync(guildId, Archiver.ModuleId))
            return;
        using var _ = logger.BeginScope(DebugLog.Guild(guildId));

        var whole = request.Whole && (request.Forced || !await RecentAsync(guildId, ct));
        var parts = whole ? Enum.GetValues<SnapshotPart>() : request.Parts.Order().ToArray();
        if (parts.Length == 0)
            return;

        var assets = new List<Asset>();
        GuildChannelList? channels = null;
        foreach (var part in parts)
        {
            try
            {
                switch (part)
                {
                    case SnapshotPart.Guild:
                        await GuildAsync(guildId, assets, ct);
                        break;
                    case SnapshotPart.Channels:
                        var startedAt = time.GetUtcNow();
                        channels = await GuildChannels.ListAsync(rest, guildId, ct);
                        archiver.OnChannelsListed(guildId, channels, startedAt);
                        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Channels, null, null);
                        break;
                    case SnapshotPart.Roles:
                        await RolesAsync(guildId, assets, ct);
                        break;
                    case SnapshotPart.Members:
                        await MembersAsync(guildId, assets, ct);
                        break;
                    case SnapshotPart.Emojis:
                        await EmojisAsync(guildId, assets, ct);
                        break;
                    case SnapshotPart.Stickers:
                        await StickersAsync(guildId, assets, ct);
                        break;
                    case SnapshotPart.Bans:
                        await BansAsync(guildId, ct);
                        break;
                    case SnapshotPart.Webhooks:
                        await WebhooksAsync(guildId, ct);
                        break;
                    case SnapshotPart.Events:
                        await EventsAsync(guildId, ct);
                        break;
                    case SnapshotPart.Pins:
                        await PinsAsync(guildId, channels, whole ? [] : request.PinChannels, ct);
                        break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is RestException or HttpRequestException or TaskCanceledException)
            {
                var problem = Problem(ex, part switch { SnapshotPart.Bans => "Ban Members", SnapshotPart.Webhooks => "Manage Webhooks", _ => null });
                logger.LogWarning("Taking the {Part} of {GuildId} failed: {Problem}", part, guildId, problem);
                archiver.OnSnapshotTaken(guildId, Kind(part), null, problem);
            }
        }

        await KeepAsync(guildId, assets, ct);
        logger.LogInformation("Snapshot of {GuildId} taken: {Parts}", guildId, string.Join(", ", parts));
    }

    private async Task<bool> RecentAsync(ulong guildId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var taken = await db.GuildSnapshots.Where(s => s.GuildId == guildId && s.Kind == SnapshotKinds.Channels).Select(s => s.TakenAt).FirstOrDefaultAsync(ct);
        return taken is { } at && time.GetUtcNow() - at < Recent;
    }

    private async Task GuildAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        var guild = await rest.GetGuildAsync(guildId, withCounts: true, cancellationToken: ct);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Guild, RawJson.OfModel(((IJsonModel<JsonGuild>)guild).JsonModel), null);
        if (guild.IconHash is { } icon && guild.GetIconUrl() is { } iconUrl)
            assets.Add(new(AssetKinds.Icon, guildId, icon, guild.Name, iconUrl.ToString(4096)));
        if (guild.BannerHash is { } banner && guild.GetBannerUrl() is { } bannerUrl)
            assets.Add(new(AssetKinds.Banner, guildId, banner, guild.Name, bannerUrl.ToString(4096)));
        if (guild.SplashHash is { } splash && guild.GetSplashUrl(ImageFormat.Png) is { } splashUrl)
            assets.Add(new(AssetKinds.Splash, guildId, splash, guild.Name, splashUrl.ToString(4096)));
    }

    private async Task RolesAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        var startedAt = time.GetUtcNow();
        var roles = await rest.GetGuildRolesAsync(guildId, cancellationToken: ct);
        archiver.OnRolesListed(guildId, roles, startedAt);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Roles, null, null);
        foreach (var role in roles)
        {
            if (role.IconHash is { } icon && role.GetIconUrl(ImageFormat.Png) is { } url)
                assets.Add(new(AssetKinds.RoleIcon, role.Id, icon, role.Name, url.ToString()));
        }
    }

    private async Task MembersAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        if (!app.Members)
        {
            archiver.OnSnapshotTaken(guildId, SnapshotKinds.Members, null, "the Server Members intent is off in the Developer Portal");
            return;
        }

        var startedAt = time.GetUtcNow();
        var members = new List<GuildUser>();
        await foreach (var member in rest.GetGuildUsersAsync(guildId, new() { BatchSize = PageSize }).WithCancellation(ct))
            members.Add(member);
        archiver.OnMembersListed(guildId, members, startedAt);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Members, null, null);
        foreach (var member in members)
        {
            if (member.AvatarHash is { } avatar && member.GetAvatarUrl() is { } url)
                assets.Add(new(AssetKinds.Avatar, member.Id, avatar, member.Username, url.ToString(1024)));
            if (member.GuildAvatarHash is { } guildAvatar && member.GetGuildAvatarUrl() is { } guildUrl)
                assets.Add(new(AssetKinds.Avatar, member.Id, guildAvatar, member.Username, guildUrl.ToString(1024)));
        }
    }

    private async Task EmojisAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        var emojis = await rest.GetGuildEmojisAsync(guildId, cancellationToken: ct);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Emojis, RawJson.OfModel(emojis.Select(e => ((IJsonModel<JsonEmoji>)e).JsonModel).ToArray()), null);
        foreach (var emoji in emojis)
            assets.Add(new(AssetKinds.Emoji, emoji.Id, "", emoji.Name ?? "", emoji.GetImageUrl(emoji.Animated ? ImageFormat.Gif : ImageFormat.Png).ToString()));
    }

    private async Task StickersAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        var stickers = await rest.GetGuildStickersAsync(guildId, cancellationToken: ct);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Stickers, RawJson.OfModel(stickers.Select(s => ((IJsonModel<JsonSticker>)s).JsonModel).ToArray()), null);
        foreach (var sticker in stickers)
        {
            var format = sticker.Format switch { StickerFormat.Lottie => ImageFormat.Lottie, StickerFormat.Gif => ImageFormat.Gif, _ => ImageFormat.Png };
            assets.Add(new(AssetKinds.Sticker, sticker.Id, "", sticker.Name, sticker.GetImageUrl(format).ToString()));
        }
    }

    private async Task BansAsync(ulong guildId, CancellationToken ct)
    {
        var bans = new List<JsonGuildBan>();
        await foreach (var ban in rest.GetGuildBansAsync(guildId, new() { BatchSize = PageSize }).WithCancellation(ct))
            bans.Add(((IJsonModel<JsonGuildBan>)ban).JsonModel);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Bans, RawJson.OfModel(bans), null);
    }

    private async Task WebhooksAsync(ulong guildId, CancellationToken ct)
    {
        var webhooks = (await rest.GetGuildWebhooksAsync(guildId, cancellationToken: ct)).Select(w => ((IJsonModel<JsonWebhook>)w).JsonModel).ToList();
        // A webhook's token lets anyone post as it; the archive keeps what it is, not the key to it.
        foreach (var webhook in webhooks)
        {
            webhook.Token = null;
            webhook.Url = null;
        }
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Webhooks, RawJson.OfModel(webhooks), null);
    }

    private async Task EventsAsync(ulong guildId, CancellationToken ct)
    {
        var events = await rest.GetGuildScheduledEventsAsync(guildId, withUserCount: true, cancellationToken: ct);
        archiver.OnSnapshotTaken(guildId, SnapshotKinds.Events, RawJson.OfModel(events.Select(e => ((IJsonModel<JsonGuildScheduledEvent>)e).JsonModel).ToArray()), null);
    }

    // Every channel's pins (listed this run, or as the gateway knows them), or only the channels named. The
    // pinned messages go through the Archiver too: fetched, they say they're pinned.
    private async Task PinsAsync(ulong guildId, GuildChannelList? channels, IReadOnlySet<ulong> only, CancellationToken ct)
    {
        IEnumerable<IGuildChannel> known = channels?.Channels
            ?? (gateway.Cache.Guilds.TryGetValue(guildId, out var guild) ? [.. guild.Channels.Values, .. guild.ActiveThreads.Values] : []);
        var ids = known.OfType<TextGuildChannel>().Select(c => c.Id).Where(id => only.Count == 0 || only.Contains(id)).Union(only).ToList();

        var pins = new Dictionary<ulong, IReadOnlyList<SeenPin>>();
        foreach (var channelId in ids)
        {
            try
            {
                var list = new List<SeenPin>();
                var askedAt = time.GetUtcNow();
                await foreach (var pin in rest.GetChannelPinsAsync(channelId, cancellationToken: ct))
                {
                    list.Add(new(pin.Message.Id, pin.PinnedAt));
                    archiver.OnFetched(guildId, pin.Message, askedAt);
                }
                if (list.Count > 0 || only.Count > 0)
                    pins[channelId] = list;
            }
            catch (RestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                // Can't be read, or gone.
            }
            await Task.Delay(PinsPace, ct);
        }
        archiver.OnPinsSeen(guildId, pins, complete: only.Count == 0);
    }

    // Saves the images not stored yet; one that failed before gets another go.
    private async Task KeepAsync(ulong guildId, List<Asset> assets, CancellationToken ct)
    {
        if (!store.Enabled || assets.Count == 0)
            return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.ArchivedAssets.Where(a => a.GuildId == guildId).ToDictionaryAsync(a => (a.Kind, a.EntityId, a.Hash), ct);
        var stored = 0;
        var failed = 0;
        foreach (var asset in assets.DistinctBy(a => (a.Kind, a.EntityId, a.Hash)))
        {
            if (known.TryGetValue((asset.Kind, asset.EntityId, asset.Hash), out var row) && row.StoredKey is not null)
                continue;
            if (row is null)
            {
                row = new() { GuildId = guildId, Kind = asset.Kind, EntityId = asset.EntityId, Hash = asset.Hash, Name = asset.Name, Url = asset.Url };
                db.ArchivedAssets.Add(row);
            }
            row.Name = asset.Name;
            row.Url = asset.Url;
            if (await FetchAsync(row, ct))
                stored++;
            else
                failed++;
            await db.SaveChangesAsync(ct);
        }
        if (stored + failed > 0)
            logger.LogInformation("Kept {Stored} images of {GuildId}; {Failed} couldn't be fetched", stored, guildId, failed);
    }

    private async Task<bool> FetchAsync(ArchivedAsset row, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);
            using var response = await http.GetAsync(row.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxAssetBytes)
                throw new InvalidOperationException($"{response.Content.Headers.ContentLength} bytes is more than an image should be");
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            row.StoredKey = await store.SaveAsync(bytes, ct);
            row.ContentType = response.Content.Headers.ContentType?.MediaType;
            row.FetchedAt = time.GetUtcNow();
            row.FailedAt = null;
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            row.FailedAt = time.GetUtcNow();
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            row.FailedAt = time.GetUtcNow();
            logger.LogWarning(ex, "Fetching the {Kind} image {EntityId} of {GuildId} failed", row.Kind, row.EntityId, row.GuildId);
            return false;
        }
    }

    private static string Kind(SnapshotPart part) => part switch
    {
        SnapshotPart.Guild => SnapshotKinds.Guild,
        SnapshotPart.Channels => SnapshotKinds.Channels,
        SnapshotPart.Roles => SnapshotKinds.Roles,
        SnapshotPart.Members => SnapshotKinds.Members,
        SnapshotPart.Emojis => SnapshotKinds.Emojis,
        SnapshotPart.Stickers => SnapshotKinds.Stickers,
        SnapshotPart.Bans => SnapshotKinds.Bans,
        SnapshotPart.Webhooks => SnapshotKinds.Webhooks,
        SnapshotPart.Events => SnapshotKinds.Events,
        _ => SnapshotKinds.Pins,
    };

    private static string Problem(Exception ex, string? permission) => ex switch
    {
        RestException { StatusCode: HttpStatusCode.Forbidden } => permission is null ? "no access" : $"no {permission} permission",
        RestException rest => $"Discord answered {(int)rest.StatusCode}",
        _ => "Discord didn't answer",
    };
}
