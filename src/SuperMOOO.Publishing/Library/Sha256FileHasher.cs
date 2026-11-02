using System.Security.Cryptography;

namespace SuperMOOO.Publishing.Library;

/// <summary>SHA-256 dạng stream; kết quả cache theo (đường dẫn, size, mtime) nên file không đổi không bị hash lại.</summary>
[Service(As = [typeof(IFileHasher)])]
public sealed class Sha256FileHasher(IFingerprintStore fingerprints) : IFileHasher
{
    public async Task<FileHash> HashAsync(string path, CancellationToken ct)
    {
        var key = PathRules.Normalize(path);
        var before = new FileInfo(key);
        if (!before.Exists) throw new FileNotFoundException("Không tìm thấy file.", key);
        var lastWrite = new DateTimeOffset(before.LastWriteTimeUtc, TimeSpan.Zero);

        var cached = await fingerprints.GetAsync(key, ct);
        if (cached is { } c && c.SizeBytes == before.Length && c.LastWriteUtc == lastWrite)
            return new FileHash(c.Sha256, before.Length, FromCache: true);

        string sha;
        await using (var stream = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

        var after = new FileInfo(key);
        if (after.Length != before.Length || after.LastWriteTimeUtc != before.LastWriteTimeUtc)
            throw new IOException("File thay đổi trong lúc tính checksum.");

        await fingerprints.UpsertAsync(key, before.Length, lastWrite, sha, ct);
        return new FileHash(sha, before.Length, FromCache: false);
    }
}
