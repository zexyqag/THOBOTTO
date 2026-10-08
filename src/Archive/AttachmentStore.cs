using System.Security.Cryptography;

using Microsoft.Extensions.Options;

namespace THOBOTTO.Archive;

public sealed class ArchiveOptions
{
    // "none" (record attachments, keep no files) or "folder". Object storage comes later.
    public string Storage { get; set; } = "none";

    // For "folder".
    public string? Folder { get; set; }
}

// Where archived attachment files are kept. Files are named by the SHA-256 of their content,
// so the same file posted many times is stored once.
public interface IAttachmentStore
{
    bool Enabled { get; }

    Task<string> SaveAsync(byte[] content, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    public static IAttachmentStore Create(IOptions<ArchiveOptions> options) => options.Value.Storage switch
    {
        "none" => new NoAttachmentStore(),
        "folder" => new FolderAttachmentStore(options.Value.Folder ?? throw new InvalidOperationException("Archive:Folder is required for folder storage")),
        var other => throw new InvalidOperationException($"Unknown Archive:Storage '{other}'"),
    };

    static string Key(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

public sealed class NoAttachmentStore : IAttachmentStore
{
    public bool Enabled => false;

    public Task<string> SaveAsync(byte[] content, CancellationToken ct) => throw new InvalidOperationException("No attachment storage is configured");

    public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;
}

public sealed class FolderAttachmentStore(string folder) : IAttachmentStore
{
    public bool Enabled => true;

    public async Task<string> SaveAsync(byte[] content, CancellationToken ct)
    {
        var key = IAttachmentStore.Key(content);
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".part";
            await File.WriteAllBytesAsync(temp, content, ct);
            File.Move(temp, path, overwrite: true);
        }
        return key;
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    // Two levels of folders by hash prefix, so no folder gets huge.
    private string PathFor(string key) => Path.Combine(folder, key[..2], key[2..4], key);
}
