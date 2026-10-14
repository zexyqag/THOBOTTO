using System.Text.Json;

namespace THOBOTTO.Archive;

// What a feature keeps of a server, for the export to carry along: every row of the server's as one JSON value
// (ids as strings, times as ISO-8601), written to bot/<Name>.json. A feature registers its source in its startup;
// the exporter collects them.
public interface IExportSource
{
    // The file's name, without extension: "quotes", "points".
    string Name { get; }

    Task WriteAsync(ulong guildId, Utf8JsonWriter json);
}

// A source whose records have files beside them (a sound's audio): it puts them in assets/<Name>/ through the
// folder, and its JSON names them by ExportFolder.PathOf.
public interface IExportFiles
{
    Task WriteFilesAsync(ulong guildId, ExportFolder folder);
}
