using System.Text.Json;

namespace THOBOTTO.Archive;

// Writes what the features keep of the server: bot/<name>.json per source, in the order the sources are registered,
// and a source's files under assets/<name>/.
public static class ExportSources
{
    private static readonly JsonWriterOptions Indented = new() { Indented = true };

    public static async Task<(IReadOnlyList<string> Names, int Files)> WriteAsync(string folder, ulong guildId, IEnumerable<IExportSource> sources, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bot"));
        var names = new List<string>();
        var files = 0;
        foreach (var source in sources)
        {
            await using (var stream = File.Create(Path.Combine(folder, "bot", $"{source.Name}.json")))
            await using (var json = new Utf8JsonWriter(stream, Indented))
            {
                await source.WriteAsync(guildId, json);
                await json.FlushAsync(ct);
            }
            files++;
            if (source is IExportFiles withFiles)
            {
                var assets = new ExportFolder(folder, source.Name);
                await withFiles.WriteFilesAsync(guildId, assets);
                files += assets.Saved;
            }
            names.Add(source.Name);
        }
        return (names, files);
    }
}
