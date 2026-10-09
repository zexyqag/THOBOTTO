using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using THOBOTTO.Helpers;
using THOBOTTO.Music;

namespace THOBOTTO.Backups;

// A server's setup as a file: modules and their settings, permissions, personalities, playlists, games,
// game servers, recurring events and voice hubs. Not members' history (points, quotes, the archive, Wrapped).
// Ids are strings (JSON numbers this big lose digits elsewhere); every channel and role referenced is
// listed with its name, so a restore into another server can find its counterpart.
public sealed record ServerBackup
{
    public const string CurrentFormat = "thobotto-backup/1";

    public string Format { get; init; } = CurrentFormat;

    public required string ServerId { get; init; }

    public required string ServerName { get; init; }

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

    public static (ServerBackup? Backup, string? Problem) Parse(string json)
    {
        try
        {
            var backup = JsonSerializer.Deserialize<ServerBackup>(json, Json);
            return backup?.Format == CurrentFormat ? (backup, null) : (null, "That isn't a THOBOTTO server backup.");
        }
        catch (JsonException ex)
        {
            return (null, $"That isn't a THOBOTTO server backup ({ex.Message.Split('.')[0]}).");
        }
    }

    [JsonIgnore]
    public string FileName => $"thobotto-{Slug(ServerName)}-{CreatedAt:yyyy-MM-dd}.json";

    private static string Slug(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } s ? s : "server";
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

public sealed record BackupGame(string Name, string? Emoji, string RoleId, string? ChannelId, int? Players, string? Server, List<BackupMode> Modes);

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
