using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileStorage.Core;
using Npgsql;
using static FileStorage.Api.MetadataStore;

namespace FileStorage.Api;

public sealed class UploadService(MetadataStore database, ObjectStore objects)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<UploadStatus> CreateAsync(string owner, UploadRequest request, string? key, CancellationToken ct)
    {
        if (Manifest.Validate(request) is { } error) throw ApiFault.Invalid(error);
        if (key is not null && !Regex.IsMatch(key, "^[a-zA-Z0-9._:-]{1,128}$")) throw ApiFault.Invalid("Invalid Idempotency-Key.");
        var payload = JsonSerializer.Serialize(request, Json);
        var digest = Chunker.Hash(Encoding.UTF8.GetBytes(payload));
        return database.WithOwnerAsync(owner, async (connection, transaction) =>
        {
            if (key is not null)
            {
                await using var existing = Command(connection, transaction,
                    "SELECT id, request_hash FROM uploads WHERE owner=$1 AND idempotency_key=$2", owner, key);
                await using var reader = await existing.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var id = reader.GetGuid(0);
                    if (reader.GetString(1) != digest) throw ApiFault.Conflict("Idempotency key was used for a different upload.");
                    await reader.DisposeAsync();
                    return await SnapshotAsync(connection, transaction, owner, id, ct);
                }
            }
            var fileId = request.FileId ?? Guid.NewGuid();
            if (request.FileId.HasValue)
            {
                await using var file = Command(connection, transaction, "SELECT 1 FROM files WHERE owner=$1 AND id=$2", owner, fileId);
                if (await file.ExecuteScalarAsync(ct) is null) throw ApiFault.Missing();
            }
            var uploadId = Guid.NewGuid();
            await using var insert = Command(connection, transaction,
                "INSERT INTO uploads(id,owner,file_id,request,request_hash,idempotency_key) VALUES($1,$2,$3,$4::jsonb,$5,$6)",
                uploadId, owner, fileId, payload, digest, key ?? Guid.NewGuid().ToString("N"));
            await insert.ExecuteNonQueryAsync(ct);
            return await SnapshotAsync(connection, transaction, owner, uploadId, ct);
        }, ct);
    }

    public async Task<UploadStatus> StatusAsync(string owner, Guid id, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        return await SnapshotAsync(connection, null, owner, id, ct);
    }

    public async Task PutAsync(string owner, Guid id, string hash, Stream body, CancellationToken ct)
    {
        if (!Manifest.IsHash(hash)) throw ApiFault.Invalid("Invalid chunk hash.");
        var data = await ObjectStore.ReadBoundedAsync(body, Chunker.MaxSize, ct);
        if (data.Length > Chunker.MaxSize) throw new ApiFault(413, "chunk_too_large", "Chunks must be at most 1 MiB.");
        if (Chunker.Hash(data) != hash) throw ApiFault.Invalid("Chunk checksum does not match.");
        await database.WithOwnerAsync(owner, async (connection, transaction) =>
        {
            var upload = await ReadUploadAsync(connection, transaction, owner, id, ct);
            upload.RequirePending();
            var chunk = upload.Request.Chunks.FirstOrDefault(c => c.Hash == hash);
            if (chunk is null || chunk.Size != data.Length) throw ApiFault.Invalid("Chunk is not in this upload's manifest.");
            await using var known = Command(connection, transaction, "SELECT size FROM chunks WHERE owner=$1 AND hash=$2", owner, hash);
            if (await known.ExecuteScalarAsync(ct) is int size && size != chunk.Size) throw ApiFault.Conflict("Chunk size conflicts with stored data.");
            await objects.PutAsync(owner, hash, data, ct);
            await using var insert = Command(connection, transaction,
                "INSERT INTO chunks(owner,hash,size) VALUES($1,$2,$3) ON CONFLICT(owner,hash) DO NOTHING", owner, hash, chunk.Size);
            await insert.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    public Task<FileVersion> CompleteAsync(string owner, Guid id, CancellationToken ct) =>
        database.WithOwnerAsync(owner, async (connection, transaction) =>
        {
            var upload = await ReadUploadAsync(connection, transaction, owner, id, ct);
            if (upload.VersionId is { } existing) return await FileQueries.ReadVersionAsync(connection, transaction, owner, upload.FileId, existing, ct);
            upload.RequirePending();
            if ((await MissingAsync(connection, transaction, owner, upload.Request, ct)).Length > 0)
                throw ApiFault.Conflict("Upload has missing chunks.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var chunk in upload.Request.Chunks)
                digest.AppendData(await objects.ReadAsync(owner, chunk, ct));
            if (Convert.ToHexStringLower(digest.GetHashAndReset()) != upload.Request.Sha256)
                throw ApiFault.Invalid("File checksum does not match the ordered chunks.");
            await using (var file = Command(connection, transaction,
                "INSERT INTO files(id,owner,name) VALUES($1,$2,$3) ON CONFLICT(id) DO UPDATE SET name=EXCLUDED.name,updated_at=clock_timestamp()",
                upload.FileId, owner, upload.Request.Name)) await file.ExecuteNonQueryAsync(ct);
            var versionId = Guid.NewGuid();
            await using (var version = Command(connection, transaction,
                "INSERT INTO versions(id,file_id,number,name,size,sha256,manifest) SELECT $1,$2,COALESCE(MAX(number),0)+1,$3,$4,$5,$6::jsonb FROM versions WHERE file_id=$2",
                versionId, upload.FileId, upload.Request.Name, upload.Request.Size, upload.Request.Sha256,
                JsonSerializer.Serialize(upload.Request.Chunks, Json))) await version.ExecuteNonQueryAsync(ct);
            await using (var finish = Command(connection, transaction,
                "UPDATE uploads SET state='completed',version_id=$1 WHERE id=$2", versionId, id)) await finish.ExecuteNonQueryAsync(ct);
            return await FileQueries.ReadVersionAsync(connection, transaction, owner, upload.FileId, versionId, ct);
        }, ct);

    public async Task AbortAsync(string owner, Guid id, CancellationToken ct)
    {
        await database.WithOwnerAsync(owner, async (connection, transaction) =>
        {
            var upload = await ReadUploadAsync(connection, transaction, owner, id, ct);
            if (upload.State == "completed") throw ApiFault.Conflict("Completed uploads cannot be aborted.");
            await using var command = Command(connection, transaction, "UPDATE uploads SET state='aborted' WHERE id=$1", id);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    private static async Task<StoredUpload> ReadUploadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string owner, Guid id, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT file_id,request::text,state,expires_at,expires_at<=clock_timestamp(),version_id FROM uploads WHERE owner=$1 AND id=$2", owner, id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ApiFault.Missing();
        return new StoredUpload(id, reader.GetGuid(0), JsonSerializer.Deserialize<UploadRequest>(reader.GetString(1), Json)!,
            reader.GetString(2), new DateTimeOffset(reader.GetDateTime(3)), reader.GetBoolean(4), reader.IsDBNull(5) ? null : reader.GetGuid(5));
    }

    private static async Task<string[]> MissingAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string owner, UploadRequest request, CancellationToken ct)
    {
        var known = new Dictionary<string, int>();
        await using var command = Command(connection, transaction,
            "SELECT hash,size FROM chunks WHERE owner=$1 AND hash=ANY($2)", owner, request.Chunks.Select(c => c.Hash).Distinct().ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) known[reader.GetString(0)] = reader.GetInt32(1);
        return request.Chunks.Where(c => !known.TryGetValue(c.Hash, out var size) || size != c.Size).Select(c => c.Hash).Distinct().ToArray();
    }

    private static async Task<UploadStatus> SnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string owner, Guid id, CancellationToken ct)
    {
        var upload = await ReadUploadAsync(connection, transaction, owner, id, ct);
        if (upload.State == "pending") upload.RequirePending();
        var missing = upload.State == "pending" ? await MissingAsync(connection, transaction, owner, upload.Request, ct) : [];
        var version = upload.VersionId is { } versionId ? await FileQueries.ReadVersionAsync(connection, transaction, owner, upload.FileId, versionId, ct) : null;
        return new UploadStatus(id, upload.FileId, upload.State, upload.ExpiresAt, missing, version);
    }
}
