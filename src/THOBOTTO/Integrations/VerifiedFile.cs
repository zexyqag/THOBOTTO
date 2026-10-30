using System.Security.Cryptography;

namespace THOBOTTO.Integrations;

// A file fetched from outside and pinned by its sum: hashed while it's written to a .partial beside its place,
// moved there when the sum matches, gone when it doesn't.
public static class VerifiedFile
{
    public static async Task SaveAsync(Stream source, string path, string sha256, Action<long> progress, CancellationToken ct)
    {
        var partial = path + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (source)
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                progress(done += read);
            }
        }
        var got = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (got != sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException($"{Path.GetFileName(path)} isn't the file expected: its sum is {got}, not {sha256}.");
        }
        File.Move(partial, path, overwrite: true);
    }
}
