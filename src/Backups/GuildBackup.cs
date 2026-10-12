using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using THOBOTTO.Helpers;

namespace THOBOTTO.Backups;

// A server's setup as a file: modules and their settings, permissions, personalities, playlists, games,
// game servers, recurring events and voice hubs. Not members' history (points, quotes, the archive, Wrapped).
// Ids are strings (JSON numbers this big lose digits elsewhere); every channel and role referenced is
// listed with its name, so a restore into another server can find its counterpart.
public sealed record GuildBackup
{
    private const string FormatPrefix = "thobotto-backup/";

    public const string CurrentFormat = FormatPrefix + "2";

    public string Format { get; init; } = CurrentFormat;

    public required string GuildId { get; init; }

    public required string GuildName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Dictionary<string, BackupChannel> Channels { get; init; } = [];

    public Dictionary<string, string> Roles { get; init; } = [];

    public List<string> Modules { get; init; } = [];

    // Module id → its settings, as each settings page stores them.
    public Dictionary<string, JsonObject> Settings { get; init; } = [];

    public bool FollowDiscord { get; init; }

    public List<BackupGrant> Permissions { get; init; } = [];

    public List<PersonalityFile> Personalities { get; init; } = [];

    public List<BackupWearer> Wearers { get; init; } = [];

    public List<BackupPlaylist> Playlists { get; init; } = [];

    public List<BackupGameServer> GameServers { get; init; } = [];

    public List<BackupGame> Games { get; init; } = [];

    public List<BackupSeries> RecurringEvents { get; init; } = [];

    public List<string> VoiceHubs { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    // Only this format: an older one's file is refused as such (its keys differ, so it wouldn't read anyway).
    public static (GuildBackup? Backup, string? Problem) Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var format = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
            if (format != CurrentFormat)
                return (null, IsOlder(format) ? "That backup is from an older THOBOTTO; make a new one." : "That isn't a THOBOTTO server backup.");
            return (document.Deserialize<GuildBackup>(Json)!, null);
        }
        catch (JsonException ex)
        {
            return (null, $"That isn't a THOBOTTO server backup ({ex.Message.Split('.')[0]}).");
        }
    }

    private static bool IsOlder(string format)
        => format.StartsWith(FormatPrefix, StringComparison.Ordinal)
            && int.TryParse(format.AsSpan(FormatPrefix.Length), out var number)
            && number < int.Parse(CurrentFormat.AsSpan(FormatPrefix.Length));

    [JsonIgnore]
    public string FileName => $"thobotto-{Slug(GuildName)}-{CreatedAt:yyyy-MM-dd}.json";

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s : "server";
}

// Text, voice or category: a restore matches names within the same kind.
public sealed record BackupChannel(string Name, string Kind);

public sealed record BackupGrant(string RoleId, string Permission);

public sealed record BackupWearer(string HelperId, string Personality);

public sealed record BackupPlaylist(string Name, string CreatorId, List<Track> Tracks);

public sealed record BackupGameServer(string Game, string Host, int? Port, string? Name, string OwnerId)
{
    public string Address => Port is null ? Host : $"{Host}:{Port}";
}

public sealed record BackupGame(string Name, string? Emoji, string RoleId, string? ChannelId, int? Players, string? GameServer, List<BackupMode> Modes);

public sealed record BackupMode(string Name, int Players);

public sealed record BackupSeries(
    string Title,
    string? Description,
    string ChannelId,
    string CreatorId,
    string? PingRoleId,
    int[] Days,
    int TimeOfDay,
    string Zone,
    int OpenDaysAhead,
    string? VoiceMode,
    bool WantsDiscordEvent,
    int? Capacity,
    string? Game,
    string? Mode,
    bool Active);
