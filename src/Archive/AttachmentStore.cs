using System.Net;
using System.Security.Cryptography;

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

using Microsoft.Extensions.Options;

namespace THOBOTTO.Archive;

public sealed class ArchiveOptions
{
    // "none" (record attachments, keep no files), "folder" or "s3".
    public string Storage { get; set; } = "none";

    // For "folder".
    public string? Folder { get; set; }

    // For "s3".
    public S3Options S3 { get; set; } = new();
}

// Any S3-compatible service; for Hetzner Object Storage the URL is https://<location>.your-objectstorage.com.
public sealed class S3Options
{
    public string? ServiceUrl { get; set; }

    public string? Bucket { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }
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

    public static IAttachmentStore Create(IOptions<ArchiveOptions> options) => options.Value.Storage switch
    {
        "none" => new NoAttachmentStore(),
        "folder" => new FolderAttachmentStore(options.Value.Folder ?? throw new InvalidOperationException("Archive:Folder is required for folder storage")),
        "s3" => options.Value.S3 is { ServiceUrl: { Length: > 0 } url, Bucket: { Length: > 0 } bucket, AccessKey: { Length: > 0 } accessKey, SecretKey: { Length: > 0 } secretKey }
            ? new S3AttachmentStore(url, bucket, accessKey, secretKey)
            : throw new InvalidOperationException("Archive:S3:ServiceUrl, Bucket, AccessKey and SecretKey are required for s3 storage"),
        var other => throw new InvalidOperationException($"Unknown Archive:Storage '{other}'"),
    };

    static string Key(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
}

public sealed class NoAttachmentStore : IAttachmentStore
{
    public bool Enabled => false;

    public string Place => "Nowhere";

    public Task<string?> ProblemAsync(CancellationToken ct) => Task.FromResult<string?>(null);

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
