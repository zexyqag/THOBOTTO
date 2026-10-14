using System.Text.Json;

namespace THOBOTTO.Archive;

// The last export of a server, kept as the JSON of its snapshot row of the export kind: when, where and how big.
public sealed record ExportRecord(DateTimeOffset At, string Folder, int Files, int Messages)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ExportRecord? Parse(GuildSnapshot? row)
        => row is { Kind: SnapshotKinds.Export, Json: { } json } ? JsonSerializer.Deserialize<ExportRecord>(json, Options) : null;
}
