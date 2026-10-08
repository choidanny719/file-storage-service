using System.Net;
using System.Net.Http.Json;
using Amazon.S3.Model;
using FileStorage.Core;

namespace FileStorage.Api.Tests;

public sealed class UploadTests(StorageFixture fixture) : IClassFixture<StorageFixture>, IAsyncLifetime
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public ValueTask InitializeAsync() => new(fixture.SqlAsync("TRUNCATE uploads,versions,files,chunks CASCADE"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2400000)]
    public async Task UploadAndDownloadPreserveBytes(int size)
    {
        using var client = fixture.Client();
        var data = Data(size);
        var (upload, request) = await Create(client, data, name: "résumé data.bin");
        Assert.Empty(await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct) ?? []);
        await SendMissing(client, upload, data);
        var version = await Complete(client, upload.Id);
        Assert.Equal(request.Sha256, version.Sha256);
        Assert.Equal(1, version.Number);
        using var response = await client.GetAsync(Content(version), Ct);
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(size, response.Content.Headers.ContentLength);
        Assert.Equal(request.Sha256, response.Headers.GetValues("X-Content-SHA256").Single());
        Assert.Contains("filename*=UTF-8", response.Content.Headers.ContentDisposition!.ToString());
        var files = await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct);
        Assert.Single(files!);
        Assert.Equal("résumé data.bin", files![0].Name);
        var status = await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct);
        Assert.Equal(version, status!.Version);
        Assert.Empty(status.Missing);
    }

    [Fact]
    public async Task ResumeAndDuplicateRequestsDoNotDuplicateVersions()
    {
        using var client = fixture.Client();
        var data = Data(3000000);
        var (upload, request) = await Create(client, data, key: "resume-1");
        var chunks = await Chunks(data);
        await Put(client, upload.Id, chunks[0]);
        await Put(client, upload.Id, chunks[0]);
        using var resumedClient = fixture.Client();
        var resumed = await resumedClient.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct);
        Assert.DoesNotContain(chunks[0].Hash, resumed!.Missing);
        Assert.Equal(upload.Missing.Length - 1, resumed.Missing.Length);
        var retry = await Create(resumedClient, request, "resume-1");
        Assert.Equal(upload.Id, retry.Id);
        await SendMissing(resumedClient, resumed, data);
        var versions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Complete(resumedClient, upload.Id)));
        Assert.All(versions, version => Assert.Equal(versions[0], version));
        var history = await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{upload.FileId}/versions", Ct);
        Assert.Single(history!);
        await fixture.SqlAsync("UPDATE uploads SET expires_at=clock_timestamp()-interval '1 hour' WHERE id=$1", upload.Id);
        Assert.Equal(versions[0], await Complete(client, upload.Id));
        Assert.Equal(versions[0], (await Create(client, request, "resume-1")).Version);
        using var abort = await client.DeleteAsync($"/v1/uploads/{upload.Id}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, abort.StatusCode);
    }

    [Fact]
    public async Task VersionsAreImmutableAndIdenticalContentIsReused()
    {
        using var client = fixture.Client();
        var original = Data(1000000);
        var (first, _) = await Create(client, original);
        await SendMissing(client, first, original);
        var v1 = await Complete(client, first.Id);
        var (second, _) = await Create(client, original, first.FileId, "renamed.bin");
        Assert.Empty(second.Missing);
        var v2 = await Complete(client, second.Id);
        var changed = Data(2000000);
        var (third, _) = await Create(client, changed, first.FileId);
        await SendMissing(client, third, changed);
        var v3 = await Complete(client, third.Id);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { v1.Number, v2.Number, v3.Number });
        Assert.Equal(original, await client.GetByteArrayAsync(Content(v1), Ct));
        Assert.Equal(original, await client.GetByteArrayAsync(Content(v2), Ct));
        Assert.Equal(changed, await client.GetByteArrayAsync(Content(v3), Ct));
        var page = await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{first.FileId}/versions?limit=1&before=3", Ct);
        Assert.Equal(v2, Assert.Single(page!));
        var files = await client.GetFromJsonAsync<FileEntry[]>("/v1/files?limit=1", Ct);
        Assert.Equal(3, Assert.Single(files!).Versions);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files?offset=1", Ct))!);
    }

    [Fact]
    public async Task ConcurrentNewVersionsHaveDistinctSequentialNumbers()
    {
        using var client = fixture.Client();
        var (initial, _) = await Create(client, []);
        await Complete(client, initial.Id);
        var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Create(client, [], initial.FileId)));
        var versions = await Task.WhenAll(sessions.Select(s => Complete(client, s.Upload.Id)));
        Assert.Equal(new[] { 2, 3, 4, 5 }, versions.Select(v => v.Number).Order());
    }

    [Fact]
    public async Task RepeatedChunkPositionsAreReconstructedInOrder()
    {
        using var client = fixture.Client();
        byte[] data = [1, 2, 3];
        var hash = Chunker.Hash(data);
        var request = new UploadRequest(null, "repeated", 6, Chunker.Hash(data.Concat(data).ToArray()), [new(hash, 3), new(hash, 3)]);
        var upload = await Create(client, request);
        Assert.Single(upload.Missing);
        await Put(client, upload.Id, new(hash, data));
        var version = await Complete(client, upload.Id);
        Assert.Equal(data.Concat(data), await client.GetByteArrayAsync(Content(version), Ct));
    }

    [Fact]
    public async Task MissingChunksAndIncorrectFullChecksumCannotCommit()
    {
        using var client = fixture.Client();
        var data = Data(100);
        var request = await Manifest.CreateAsync(new MemoryStream(data), "file", cancellationToken: Ct);
        var upload = await Create(client, request with { Sha256 = Chunker.Hash([9]) });
        using var missing = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        await SendMissing(client, upload, data);
        using var mismatch = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        Assert.Equal("pending", (await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!.State);
    }

    [Theory]
    [InlineData("bad-hash")]
    [InlineData("wrong-bytes")]
    [InlineData("not-in-manifest")]
    [InlineData("oversized")]
    [InlineData("wrong-size")]
    public async Task InvalidChunkBodiesAreRejected(string scenario)
    {
        using var client = fixture.Client();
        byte[] data = [1, 2, 3];
        var request = await Manifest.CreateAsync(new MemoryStream(data), "file", cancellationToken: Ct);
        if (scenario == "wrong-size") request = request with { Size = 4, Chunks = [new(request.Sha256, 4)] };
        var upload = await Create(client, request);
        var body = scenario switch { "oversized" => new byte[Chunker.MaxSize + 1], "wrong-bytes" or "not-in-manifest" => [4, 5, 6], _ => data };
        var hash = scenario switch { "bad-hash" => "bad", "not-in-manifest" or "oversized" => Chunker.Hash(body), _ => request.Sha256 };
        using var response = await client.PutAsync($"/v1/uploads/{upload.Id}/chunks/{hash}", new ByteArrayContent(body), Ct);
        Assert.Equal(scenario == "oversized" ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!.Missing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredAndAbortedSessionsRejectWrites(bool expired)
    {
        using var client = fixture.Client();
        var (upload, request) = await Create(client, [1]);
        if (expired) await fixture.SqlAsync("UPDATE uploads SET expires_at=clock_timestamp()-interval '1 hour' WHERE id=$1", upload.Id);
        else
        {
            using var first = await client.DeleteAsync($"/v1/uploads/{upload.Id}", Ct);
            using var second = await client.DeleteAsync($"/v1/uploads/{upload.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(first.StatusCode, second.StatusCode);
        }
        var expected = expired ? HttpStatusCode.Gone : HttpStatusCode.Conflict;
        using var put = await client.PutAsync($"/v1/uploads/{upload.Id}/chunks/{request.Sha256}", new ByteArrayContent([1]), Ct);
        using var finish = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        Assert.Equal(expected, put.StatusCode);
        Assert.Equal(expected, finish.StatusCode);
    }

    [Fact]
    public async Task UsersCannotAccessEachOthersFilesSessionsOrChunks()
    {
        using var alice = fixture.Client();
        using var bob = fixture.Client(StorageFixture.BobToken);
        var (upload, request) = await Create(alice, [1, 2, 3]);
        await SendMissing(alice, upload, [1, 2, 3]);
        var version = await Complete(alice, upload.Id);
        var responses = new[] {
            await bob.GetAsync($"/v1/uploads/{upload.Id}", Ct),
            await bob.DeleteAsync($"/v1/uploads/{upload.Id}", Ct),
            await bob.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct),
            await bob.PutAsync($"/v1/uploads/{upload.Id}/chunks/{request.Sha256}", new ByteArrayContent([1, 2, 3]), Ct),
            await bob.GetAsync($"/v1/files/{upload.FileId}/versions", Ct),
            await bob.GetAsync(Content(version), Ct),
            await bob.PostAsJsonAsync("/v1/uploads", request with { FileId = upload.FileId }, Ct)
        };
        foreach (var response in responses) { Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); response.Dispose(); }
        Assert.Empty((await bob.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        var bobUpload = await Create(bob, request);
        Assert.Single(bobUpload.Missing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect")]
    public async Task AuthenticationIsRequiredButHealthIsPublic(string? token)
    {
        using var client = fixture.Client(token);
        using var response = await client.GetAsync("/v1/files", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        using var health = await client.GetAsync("/health/live", Ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("/v1/files?offset=-1")]
    [InlineData("/v1/files?limit=0")]
    [InlineData("/v1/files?limit=101")]
    [InlineData("/v1/files?limit=oops")]
    [InlineData("/v1/files/11111111-1111-1111-1111-111111111111/versions?before=0")]
    public async Task InvalidPaginationIsRejected(string route)
    {
        using var client = fixture.Client();
        using var response = await client.GetAsync(route, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKeysCannotBeReusedForDifferentRequests()
    {
        using var client = fixture.Client();
        var (_, request) = await Create(client, [], key: "same-key");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request with { Name = "different" }) };
        message.Headers.Add("Idempotency-Key", "same-key");
        using var response = await client.SendAsync(message, Ct);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var bad = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request) };
        bad.Headers.Add("Idempotency-Key", "contains spaces");
        using var invalid = await client.SendAsync(bad, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{broken")]
    [InlineData("{\"name\":\"../bad\",\"size\":0,\"sha256\":\"bad\",\"chunks\":[]}")]
    public async Task MalformedManifestsAreRejected(string json)
    {
        using var client = fixture.Client();
        using var response = await client.PostAsync("/v1/uploads", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptObjectsBlockCompletionAndCanBeRepaired(bool corrupt)
    {
        using var client = fixture.Client();
        byte[] data = [7, 8, 9];
        var (upload, request) = await Create(client, data);
        await SendMissing(client, upload, data);
        var key = $"chunks/alice/{request.Sha256}";
        if (corrupt)
            await fixture.S3.PutObjectAsync(new PutObjectRequest { BucketName = fixture.Bucket, Key = key, ContentBody = "bad" }, Ct);
        else await fixture.S3.DeleteObjectAsync(fixture.Bucket, key, Ct);
        using var failed = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        await Put(client, upload.Id, new(request.Sha256, data));
        var version = await Complete(client, upload.Id);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
        await fixture.S3.DeleteObjectAsync(fixture.Bucket, key, Ct);
        using var download = await client.GetAsync(Content(version), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
    }

    [Fact]
    public async Task DatabaseFailureRollsBackCompletionAndRetryCommitsOnce()
    {
        using var client = fixture.Client();
        var (upload, _) = await Create(client, []);
        await fixture.SqlAsync("ALTER TABLE versions ADD CONSTRAINT reject_test CHECK (number<0)");
        try
        {
            using var response = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        }
        finally { await fixture.SqlAsync("ALTER TABLE versions DROP CONSTRAINT reject_test"); }
        Assert.Equal(1, (await Complete(client, upload.Id)).Number);
    }

    private async Task<(UploadStatus Upload, UploadRequest Request)> Create(HttpClient client, byte[] data, Guid? fileId = null, string name = "file.bin", string? key = null)
    {
        var request = await Manifest.CreateAsync(new MemoryStream(data), name, fileId, Ct);
        return (await Create(client, request, key), request);
    }

    private async Task<UploadStatus> Create(HttpClient client, UploadRequest request, string? key = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request) };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(message, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UploadStatus>(Ct))!;
    }

    private async Task SendMissing(HttpClient client, UploadStatus upload, byte[] data)
    {
        var missing = upload.Missing.ToHashSet();
        foreach (var chunk in await Chunks(data)) if (missing.Remove(chunk.Hash)) await Put(client, upload.Id, chunk);
    }

    private async Task Put(HttpClient client, Guid upload, Chunk chunk)
    {
        using var response = await client.PutAsync($"/v1/uploads/{upload}/chunks/{chunk.Hash}", new ByteArrayContent(chunk.Data), Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<FileVersion> Complete(HttpClient client, Guid upload)
    {
        using var response = await client.PostAsync($"/v1/uploads/{upload}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileVersion>(Ct))!;
    }

    private async Task<List<Chunk>> Chunks(byte[] data)
    {
        var chunks = new List<Chunk>();
        await foreach (var chunk in Chunker.ReadAsync(new MemoryStream(data), Ct)) chunks.Add(chunk);
        return chunks;
    }

    private static byte[] Data(int size) { var bytes = new byte[size]; new Random(12).NextBytes(bytes); return bytes; }
    private static string Content(FileVersion version) => $"/v1/files/{version.FileId}/versions/{version.Id}/content";
}
