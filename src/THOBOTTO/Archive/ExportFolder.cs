using System.Buffers;

namespace THOBOTTO.Archive;

// Where a source puts the files beside its records, assets/<source>/ in the export; the path PathOf answers is the one
// its JSON names, relative to the export's root.
public sealed class ExportFolder(string root, string source)
{
    // What every file system takes of a name, no longer than this, so the hash or id beside it keeps paths short.
    private const int LongestName = 42;

    // What Windows refuses in a name, whatever system the export is written on: it's copied anywhere.
    private static readonly SearchValues<char> Unsafe = SearchValues.Create("\\/:*?\"<>|");

    public int Saved { get; private set; }

    public static string PathOf(string source, string fileName) => $"assets/{source}/{fileName}";

    public async Task SaveAsync(string fileName, byte[] content)
    {
        var path = Path.Combine(root, "assets", source, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        Saved++;
    }

    public static string Safe(string? name)
    {
        var safe = string.Concat((name ?? "").Select(c => c < ' ' || Unsafe.Contains(c) ? '_' : c)).Trim();
        return safe.Length > LongestName ? safe[..LongestName] : safe;
    }
}
