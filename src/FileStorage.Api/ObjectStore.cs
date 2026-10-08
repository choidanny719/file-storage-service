using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using FileStorage.Core;

namespace FileStorage.Api;

public sealed class ObjectStore(IAmazonS3 s3, StorageSettings settings)
{
    private static string Key(string owner, string hash) => $"chunks/{owner}/{hash}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        try { await s3.GetBucketLocationAsync(settings.Bucket, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound && settings.CreateBucket)
        {
            try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = settings.Bucket }, ct); }
            catch (AmazonS3Exception collision) when (collision.ErrorCode == "BucketAlreadyOwnedByYou") { }
        }
    }

    public async Task PutAsync(string owner, string hash, byte[] data, CancellationToken ct)
    {
        await using var stream = new MemoryStream(data, false);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.Bucket, Key = Key(owner, hash), InputStream = stream,
            ContentType = "application/octet-stream", UseChunkEncoding = false,
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(hash))
        }, ct);
    }

    public async Task<byte[]> ReadAsync(string owner, ChunkInfo chunk, CancellationToken ct)
    {
        using var result = await s3.GetObjectAsync(settings.Bucket, Key(owner, chunk.Hash), ct);
        var bytes = await ReadBoundedAsync(result.ResponseStream, chunk.Size, ct);
        if (bytes.Length != chunk.Size || Chunker.Hash(bytes) != chunk.Hash)
            throw new ApiFault(503, "storage_integrity_error", "A stored chunk failed integrity validation.");
        return bytes;
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int expected, CancellationToken ct)
    {
        var bytes = new byte[expected + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        return bytes.AsSpan(0, count).ToArray();
    }
}
