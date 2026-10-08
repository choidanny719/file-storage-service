using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FileStorage.Core;

namespace FileStorage.Cli;

public sealed class StorageClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record ResumeState(string Server, UploadRequest Request, string Key, Guid? UploadId);

    public async Task<FileVersion> UploadAsync(string path, Guid? fileId, string statePath, CancellationToken ct)
    {
        var server = http.BaseAddress!.AbsoluteUri;
        await using var stateLock = new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var request = await Manifest.CreateAsync(file, Path.GetFileName(path), fileId, ct);
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<ResumeState>(await File.ReadAllTextAsync(statePath, ct), Json) ?? throw new InvalidDataException("Invalid resume state.")
            : new ResumeState(server, request, Guid.NewGuid().ToString("N"), null);
        if (state.Server != server || JsonSerializer.Serialize(state.Request, Json) != JsonSerializer.Serialize(request, Json))
            throw new InvalidDataException("Resume state belongs to a different file, version target, or server. Use a new --state path.");
        await SaveAsync(statePath, state, ct);
        UploadStatus status;
        if (state.UploadId is { } id)
            status = await GetAsync<UploadStatus>($"v1/uploads/{id}", ct);
        else
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/uploads") { Content = JsonContent.Create(request) };
            message.Headers.Add("Idempotency-Key", state.Key);
            using var response = await http.SendAsync(message, ct);
            status = await ReadAsync<UploadStatus>(response, ct);
            state = state with { UploadId = status.Id };
            await SaveAsync(statePath, state, ct);
        }
        if (status.State == "aborted") throw new InvalidOperationException("Upload was aborted. Use a new --state path.");
        if (status.Version is { } completed)
        {
            File.Delete(statePath);
            return completed;
        }
        var missing = status.Missing.ToHashSet(StringComparer.Ordinal);
        file.Position = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var chunk in Chunker.ReadAsync(file, ct))
        {
            digest.AppendData(chunk.Data);
            if (!missing.Remove(chunk.Hash)) continue;
            using var content = new ByteArrayContent(chunk.Data);
            using var response = await http.PutAsync($"v1/uploads/{status.Id}/chunks/{chunk.Hash}", content, ct);
            await EnsureSuccessAsync(response, ct);
        }
        if (missing.Count > 0 || Convert.ToHexStringLower(digest.GetHashAndReset()) != request.Sha256)
            throw new InvalidDataException("File changed during upload. Use a new --state path.");
        using var finish = await http.PostAsync($"v1/uploads/{status.Id}/complete", null, ct);
        var version = await ReadAsync<FileVersion>(finish, ct);
        File.Delete(statePath);
        return version;
    }

    public async Task DownloadAsync(Guid fileId, Guid versionId, string destination, CancellationToken ct)
    {
        if (File.Exists(destination)) throw new IOException("Destination already exists.");
        using var response = await http.GetAsync($"v1/files/{fileId}/versions/{versionId}/content", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var expected = response.Headers.TryGetValues("X-Content-SHA256", out var values) ? values.SingleOrDefault() : null;
        if (!Manifest.IsHash(expected)) throw new InvalidDataException("Server did not provide a valid checksum.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    digest.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (Convert.ToHexStringLower(digest.GetHashAndReset()) != expected)
                    throw new InvalidDataException("Download checksum does not match.");
            }
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var response = await http.GetAsync(route, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidDataException("Server returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }

    private static async Task SaveAsync(string path, ResumeState state, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Json), ct);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
