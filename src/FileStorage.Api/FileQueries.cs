using System.Text.Json;
using FileStorage.Core;
using Npgsql;
using static FileStorage.Api.MetadataStore;

namespace FileStorage.Api;

public sealed class FileQueries(MetadataStore database, ObjectStore objects)
{
    public async Task<List<FileEntry>> ListAsync(string owner, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100) throw ApiFault.Invalid("Use offset >= 0 and limit between 1 and 100.");
        await using var connection = await database.OpenAsync(ct);
        await using var command = Command(connection, null,
            "SELECT f.id,f.name,COUNT(v.id)::int,f.updated_at FROM files f JOIN versions v ON v.file_id=f.id WHERE f.owner=$1 GROUP BY f.id ORDER BY f.updated_at DESC,f.id LIMIT $2 OFFSET $3",
            owner, limit, offset);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FileEntry>();
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetDateTime(3)));
        return result;
    }

    public async Task<List<FileVersion>> VersionsAsync(string owner, Guid fileId, int before, int limit, CancellationToken ct)
    {
        if (before < 1 || limit is < 1 or > 100) throw ApiFault.Invalid("Use before >= 1 and limit between 1 and 100.");
        await using var connection = await database.OpenAsync(ct);
        await using (var exists = Command(connection, null, "SELECT 1 FROM files WHERE id=$1 AND owner=$2", fileId, owner))
            if (await exists.ExecuteScalarAsync(ct) is null) throw ApiFault.Missing();
        await using var command = Command(connection, null,
            "SELECT id,file_id,number,name,size,sha256,created_at FROM versions WHERE file_id=$1 AND number<$2 ORDER BY number DESC LIMIT $3", fileId, before, limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<FileVersion>();
        while (await reader.ReadAsync(ct)) result.Add(ReadVersion(reader));
        return result;
    }

    internal static async Task<FileVersion> ReadVersionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string owner, Guid fileId, Guid versionId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT v.id,v.file_id,v.number,v.name,v.size,v.sha256,v.created_at FROM versions v JOIN files f ON f.id=v.file_id WHERE f.owner=$1 AND f.id=$2 AND v.id=$3", owner, fileId, versionId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw ApiFault.Missing();
        return ReadVersion(reader);
    }

    public async Task DownloadAsync(HttpContext context, string owner, Guid fileId, Guid versionId)
    {
        var ct = context.RequestAborted;
        FileVersion version;
        ChunkInfo[] chunks;
        await using (var connection = await database.OpenAsync(ct))
        {
            version = await ReadVersionAsync(connection, null, owner, fileId, versionId, ct);
            await using var command = Command(connection, null, "SELECT manifest::text FROM versions WHERE id=$1", versionId);
            chunks = JsonSerializer.Deserialize<ChunkInfo[]>((string)(await command.ExecuteScalarAsync(ct))!, UploadService.Json)!;
        }
        var first = chunks.Length == 0 ? [] : await objects.ReadAsync(owner, chunks[0], ct);
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = version.Size;
        context.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(version.Name)}";
        context.Response.Headers["X-Content-SHA256"] = version.Sha256;
        context.Response.Headers.ETag = $"\"{version.Sha256}\"";
        await context.Response.Body.WriteAsync(first, ct);
        foreach (var chunk in chunks.Skip(1))
            await context.Response.Body.WriteAsync(await objects.ReadAsync(owner, chunk, ct), ct);
    }

    private static FileVersion ReadVersion(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2),
        reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetDateTime(6));
}
