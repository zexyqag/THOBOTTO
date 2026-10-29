using System.Net;
using System.Security.Cryptography;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using THOBOTTO.Integrations;

namespace THOBOTTO.Archive;

// The archive's file store as the Integrations page (or the environment) has it, rebuilt when those settings change.
public sealed class AttachmentStorage(IntegrationStore settings, IConfiguration config) : IAttachmentStore
{
    private sealed record Choice(string? Storage, string? Folder, string? Url, string? Bucket, string? AccessKey, string? SecretKey);

    private readonly Lock _sync = new();
    private (Choice Choice, IAttachmentStore Store)? _built;

    private IAttachmentStore Current
    {
        get
        {
            var choice = new Choice(settings.Get(IntegrationStore.ArchiveStorage), config["Archive:Folder"], settings.Get(IntegrationStore.ArchiveS3Url),
                settings.Get(IntegrationStore.ArchiveS3Bucket), settings.Get(IntegrationStore.ArchiveS3AccessKey), settings.Get(IntegrationStore.ArchiveS3SecretKey));
            lock (_sync)
            {
                if (_built?.Choice != choice)
                    _built = (choice, Build(choice));
                return _built.Value.Store;
            }
        }
    }

    public bool Enabled => Current.Enabled;

    public string Place => Current.Place;

    public Task<string?> ProblemAsync(CancellationToken ct) => Current.ProblemAsync(ct);

    public Task<string> SaveAsync(byte[] content, CancellationToken ct) => Current.SaveAsync(content, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => Current.DeleteAsync(key, ct);

    public Task<Stream?> OpenAsync(string key, CancellationToken ct) => Current.OpenAsync(key, ct);

    private static IAttachmentStore Build(Choice c) => c.Storage switch
    {
        null or "none" => new NoAttachmentStore(),
        "folder" => c.Folder is { Length: > 0 } folder ? new FolderAttachmentStore(folder) : new NoAttachmentStore("Archive:Folder isn't set for this bot."),
        "s3" => c is { Url: { Length: > 0 } url, Bucket: { Length: > 0 } bucket, AccessKey: { Length: > 0 } access, SecretKey: { Length: > 0 } secret }
            ? new S3AttachmentStore(url, bucket, access, secret)
            : new NoAttachmentStore("S3 needs its address, bucket, access key and secret key."),
        var other => new NoAttachmentStore($"\"{other}\" isn't none, folder or s3."),
    };
}

// Where archived attachment files are kept. Files are named by the SHA-256 of their content,
// so the same file posted many times is stored once.
public interface IAttachmentStore
{
    bool Enabled { get; }

    // Where the files go, for the Integrations page.
    string Place { get; }

    // What's wrong with the storage, or null when it answers.
    Task<string?> ProblemAsync(CancellationToken ct);

    Task<string> SaveAsync(byte[] content, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    // Null when the file isn't there.
    Task<Stream?> OpenAsync(string key, CancellationToken ct);

    static string Key(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

// No files kept; with a problem when storage was chosen but can't be used.
public sealed class NoAttachmentStore(string? problem = null) : IAttachmentStore
{
    public bool Enabled => false;

    public string Place => "Nowhere";

    public Task<string?> ProblemAsync(CancellationToken ct) => Task.FromResult(problem);

    public Task<string> SaveAsync(byte[] content, CancellationToken ct) => throw new InvalidOperationException("No attachment storage is configured");

    public Task DeleteAsync(string key, CancellationToken ct) => Task.CompletedTask;

    public Task<Stream?> OpenAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(null);
}

public sealed class FolderAttachmentStore(string folder) : IAttachmentStore
{
    public bool Enabled => true;

    public string Place => $"Folder {folder}";

    public Task<string?> ProblemAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

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

    public Task<Stream?> OpenAsync(string key, CancellationToken ct) => Task.FromResult<Stream?>(File.Exists(PathFor(key)) ? File.OpenRead(PathFor(key)) : null);

    // Two levels of folders by hash prefix, so no folder gets huge.
    private string PathFor(string key) => Path.Combine(folder, key[..2], key[2..4], key);
}

// Objects named by the same content key, flat in one bucket.
public sealed class S3AttachmentStore(string serviceUrl, string bucket, string accessKey, string secretKey) : IAttachmentStore
{
    // Checksums only where S3 demands them: the SDK's newer kinds aren't taken by other services.
    private readonly AmazonS3Client _client = new(accessKey, secretKey, new AmazonS3Config
    {
        ServiceURL = serviceUrl,
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    });

    public bool Enabled => true;

    public string Place => $"Bucket {bucket} at {serviceUrl}";

    public async Task<string?> ProblemAsync(CancellationToken ct)
    {
        try
        {
            await _client.HeadBucketAsync(new() { BucketName = bucket }, ct);
            return null;
        }
        catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    public async Task<string> SaveAsync(byte[] content, CancellationToken ct)
    {
        var key = IAttachmentStore.Key(content);
        if (await ExistsAsync(key, ct))
            return key;
        // The file is in memory already, so a plain signed PUT, which every S3-alike takes, costs nothing.
        using var stream = new MemoryStream(content);
        await _client.PutObjectAsync(new() { BucketName = bucket, Key = key, InputStream = stream, UseChunkEncoding = false }, ct);
        return key;
    }

    public Task DeleteAsync(string key, CancellationToken ct) => _client.DeleteObjectAsync(bucket, key, ct);

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        try
        {
            return (await _client.GetObjectAsync(bucket, key, ct)).ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
