using System.Security.Cryptography;

namespace FileStorage.Core;

public sealed record ChunkInfo(string Hash, int Size);
public sealed record UploadRequest(Guid? FileId, string Name, long Size, string Sha256, ChunkInfo[] Chunks);
public sealed record FileVersion(Guid Id, Guid FileId, int Number, string Name, long Size, string Sha256, DateTimeOffset CreatedAt);
public sealed record FileEntry(Guid Id, string Name, int Versions, DateTimeOffset UpdatedAt);
public sealed record UploadStatus(Guid Id, Guid FileId, string State, DateTimeOffset ExpiresAt, string[] Missing, FileVersion? Version);

public static class Manifest
{
    public const long MaxFileSize = 512L * 1024 * 1024;
    public const int MaxChunks = 8192;

    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? Validate(UploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200 ||
            request.Name is "." or ".." || request.Name.Any(c => char.IsControl(c) || c is '/' or '\\'))
            return "Name must be a file name of 1..200 characters.";
        if (request.FileId == Guid.Empty) return "FileId cannot be empty.";
        if (request.Size < 0 || request.Size > MaxFileSize) return "Files must be at most 512 MiB.";
        if (!IsHash(request.Sha256)) return "Sha256 must be a lowercase SHA-256 digest.";
        if (request.Chunks is null || request.Chunks.Length > MaxChunks) return "Too many chunks.";
        long size = 0;
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chunk in request.Chunks)
        {
            if (chunk is null || !IsHash(chunk.Hash) || chunk.Size < 1 || chunk.Size > Chunker.MaxSize)
                return "Invalid chunk hash or size.";
            if (sizes.TryGetValue(chunk.Hash, out var previous) && previous != chunk.Size)
                return "Repeated chunk hashes must have the same size.";
            sizes[chunk.Hash] = chunk.Size;
            size += chunk.Size;
        }
        if (size != request.Size) return "Chunk sizes must add up to the file size.";
        if (size == 0 && request.Sha256 != Chunker.Hash([])) return "Invalid empty-file checksum.";
        return null;
    }

    public static async Task<UploadRequest> CreateAsync(Stream input, string name, Guid? fileId = null,
        CancellationToken cancellationToken = default)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new List<ChunkInfo>();
        long size = 0;
        await foreach (var chunk in Chunker.ReadAsync(input, cancellationToken))
        {
            size += chunk.Data.Length;
            if (size > MaxFileSize || chunks.Count == MaxChunks) throw new InvalidDataException("File exceeds upload limits.");
            digest.AppendData(chunk.Data);
            chunks.Add(new ChunkInfo(chunk.Hash, chunk.Data.Length));
        }
        var result = new UploadRequest(fileId, name, size, Convert.ToHexStringLower(digest.GetHashAndReset()), chunks.ToArray());
        if (Validate(result) is { } error) throw new InvalidDataException(error);
        return result;
    }
}
