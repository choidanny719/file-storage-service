using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Amazon.S3.Model;
using FileStorage.Core;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FileStorage.Api.Tests;

public sealed partial class UploadTests
{
    [Fact]
    public async Task PartialReuseAndOutOfOrderWritesPreserveManifestOrder()
    {
        using var client = fixture.Client();
        byte[] a = [1, 2], b = [3, 4], c = [5, 6];
        var (original, _) = await Create(client, a);
        await SendMissing(client, original, a);
        await Complete(client, original.Id);
        var data = a.Concat(b).Concat(a).Concat(c).ToArray();
        var request = new UploadRequest(original.FileId, "combined", data.Length, Chunker.Hash(data),
            [new(Chunker.Hash(a), a.Length), new(Chunker.Hash(b), b.Length), new(Chunker.Hash(a), a.Length), new(Chunker.Hash(c), c.Length)]);
        var upload = await Create(client, request);
        Assert.Equal(new[] { Chunker.Hash(b), Chunker.Hash(c) }, upload.Missing);
        await Put(client, upload.Id, new(Chunker.Hash(c), c));
        var status = await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct);
        Assert.Equal(Chunker.Hash(b), Assert.Single(status!.Missing));
        await Put(client, upload.Id, new(Chunker.Hash(b), b));
        var version = await Complete(client, upload.Id);
        Assert.Equal(2, version.Number);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
    }

    [Fact]
    public async Task ExactlyOneMiBChunkIsAccepted()
    {
        using var client = fixture.Client();
        var data = Data(Chunker.MaxSize);
        var hash = Chunker.Hash(data);
        var upload = await Create(client, new UploadRequest(null, "maximum-chunk", data.Length, hash, [new(hash, data.Length)]));
        await Put(client, upload.Id, new(hash, data));
        var version = await Complete(client, upload.Id);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSessionsReuseChunksFromAbortedOrExpiredUploads(bool expired)
    {
        using var client = fixture.Client();
        byte[] data = [21, 22, 23];
        var (old, request) = await Create(client, data, key: "reserved-key");
        await SendMissing(client, old, data);
        if (expired) await fixture.SqlAsync("UPDATE uploads SET expires_at=clock_timestamp()-interval '1 hour' WHERE id=$1", old.Id);
        else
        {
            using var abort = await client.DeleteAsync($"/v1/uploads/{old.Id}", Ct);
            Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
            var aborted = await Create(client, request, "reserved-key");
            Assert.Equal(old.Id, aborted.Id);
            Assert.Equal("aborted", aborted.State);
            Assert.Empty(aborted.Missing);
            Assert.Null(aborted.Version);
        }
        using var reusedKey = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request) };
        reusedKey.Headers.Add("Idempotency-Key", "reserved-key");
        using var retry = await client.SendAsync(reusedKey, Ct);
        Assert.Equal(expired ? HttpStatusCode.Gone : HttpStatusCode.Created, retry.StatusCode);
        var fresh = await Create(client, request, "fresh-key");
        Assert.NotEqual(old.Id, fresh.Id);
        Assert.Empty(fresh.Missing);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(await Complete(client, fresh.Id)), Ct));
    }

    [Fact]
    public async Task ConcurrentDuplicateChunkWritesRemainRetryable()
    {
        using var client = fixture.Client();
        byte[] data = [31, 32, 33];
        var (upload, request) = await Create(client, data);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Put(client, upload.Id, new(request.Sha256, data))));
        Assert.Empty((await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!.Missing);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(await Complete(client, upload.Id)), Ct));
    }

    [Fact]
    public async Task FailedNewVersionDoesNotRenameOrReplaceTheExistingFile()
    {
        using var client = fixture.Client();
        var (first, _) = await Create(client, [41], name: "original.bin");
        await SendMissing(client, first, [41]);
        var original = await Complete(client, first.Id);
        var (second, _) = await Create(client, [42], first.FileId, "renamed.bin");
        await SendMissing(client, second, [42]);
        await fixture.SqlAsync("ALTER TABLE versions ADD CONSTRAINT reject_second CHECK (number<2)");
        try
        {
            using var failure = await client.PostAsync($"/v1/uploads/{second.Id}/complete", null, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            var file = Assert.Single((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
            Assert.Equal("original.bin", file.Name);
            Assert.Equal(1, file.Versions);
            Assert.Equal(original, Assert.Single((await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{first.FileId}/versions", Ct))!));
            Assert.Equal(new byte[] { 41 }, await client.GetByteArrayAsync(Content(original), Ct));
        }
        finally { await fixture.SqlAsync("ALTER TABLE versions DROP CONSTRAINT reject_second"); }
        var retry = await Complete(client, second.Id);
        Assert.Equal(2, retry.Number);
        Assert.Equal("renamed.bin", retry.Name);
    }

    [Fact]
    public async Task FilePaginationHasStableTieOrderingAndVersionsHaveAnEnd()
    {
        using var client = fixture.Client();
        var versions = new List<FileVersion>();
        foreach (var name in new[] { "first", "second", "third" })
        {
            var (upload, _) = await Create(client, [], name: name);
            versions.Add(await Complete(client, upload.Id));
        }
        await fixture.SqlAsync("UPDATE files SET updated_at='2026-01-01T00:00:00Z'");
        var ids = new List<Guid>();
        for (var offset = 0; offset < 3; offset++)
        {
            var page = (await client.GetFromJsonAsync<FileEntry[]>($"/v1/files?offset={offset}&limit=1", Ct))!;
            ids.Add(Assert.Single(page).Id);
        }
        Assert.Equal(versions.Select(v => v.FileId).OrderBy(id => id.ToString("N"), StringComparer.Ordinal), ids);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files?offset=3&limit=100", Ct))!);
        Assert.Empty((await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{ids[0]}/versions?before=1&limit=100", Ct))!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task VersionPaginationRejectsInvalidLimits(int limit)
    {
        using var client = fixture.Client();
        var (upload, _) = await Create(client, []);
        await Complete(client, upload.Id);
        using var response = await client.GetAsync($"/v1/files/{upload.FileId}/versions?limit={limit}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AVersionCannotBeDownloadedThroughAnotherFileId()
    {
        using var client = fixture.Client();
        var (first, _) = await Create(client, []);
        var (second, _) = await Create(client, []);
        var version = await Complete(client, first.Id);
        await Complete(client, second.Id);
        using var response = await client.GetAsync($"/v1/files/{second.FileId}/versions/{version.Id}/content", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task IdempotencyKeyLengthBoundariesAreAccepted(int length)
    {
        using var client = fixture.Client();
        var request = await Manifest.CreateAsync(Stream.Null, "empty", cancellationToken: Ct);
        var key = new string('k', length);
        Assert.Equal((await Create(client, request, key)).Id, (await Create(client, request, key)).Id);
    }

    [Fact]
    public async Task TooLongIdempotencyKeysCannotCreateSessions()
    {
        using var client = fixture.Client();
        var request = await Manifest.CreateAsync(Stream.Null, "empty", cancellationToken: Ct);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", new string('k', 129));
        using var response = await client.SendAsync(message, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("bearer", true)]
    [InlineData("BEARER", true)]
    [InlineData("Basic", false)]
    public async Task AuthenticationRequiresTheBearerScheme(string scheme, bool accepted)
    {
        using var client = fixture.Client(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, StorageFixture.AliceToken);
        using var response = await client.GetAsync("/v1/files", Ct);
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task TruncatedOrOversizedStoredChunksFailIntegrityChecks(int storedSize)
    {
        using var client = fixture.Client();
        byte[] data = [51, 52, 53];
        var (upload, request) = await Create(client, data);
        await SendMissing(client, upload, data);
        await fixture.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = fixture.Bucket, Key = $"chunks/alice/{request.Sha256}", ContentBody = new string('x', storedSize)
        }, Ct);
        using var response = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        await Put(client, upload.Id, new(request.Sha256, data));
        Assert.Equal(data, await client.GetByteArrayAsync(Content(await Complete(client, upload.Id)), Ct));
    }

    [Fact]
    public async Task ObjectStoreWriteFailureLeavesTheChunkMissingForRetry()
    {
        using var client = fixture.Client();
        byte[] data = [61, 62, 63];
        var (upload, request) = await Create(client, data);
        var settings = fixture.App.Services.GetRequiredService<StorageSettings>();
        var bucket = settings.Bucket;
        settings.Bucket = "missing-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await client.PutAsync($"/v1/uploads/{upload.Id}/chunks/{request.Sha256}", new ByteArrayContent(data), Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(request.Sha256, Assert.Single((await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!.Missing));
        }
        finally { settings.Bucket = bucket; }
        await Put(client, upload.Id, new(request.Sha256, data));
        Assert.Equal(data, await client.GetByteArrayAsync(Content(await Complete(client, upload.Id)), Ct));
    }

    [Fact]
    public async Task ConcurrencyLimitIsPerUserAndPermitsAreReleased()
    {
        using var alice = fixture.Client();
        using var bob = fixture.Client(StorageFixture.BobToken);
        await using var gate = new NpgsqlConnection(fixture.ConnectionString);
        await gate.OpenAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('alice',0))", gate))
            await hold.ExecuteNonQueryAsync(Ct);
        var pending = Enumerable.Range(0, 8).Select(_ => Create(alice, [])).ToArray();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await using var probe = new NpgsqlConnection(fixture.ConnectionString);
            await probe.OpenAsync(deadline.Token);
            await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name=current_setting('application_name') AND wait_event='advisory'", probe);
            while ((long)(await waiting.ExecuteScalarAsync(deadline.Token))! < 8)
                await Task.Delay(10, deadline.Token);
            using var limited = await alice.GetAsync("/v1/files", Ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            var (other, _) = await Create(bob, []);
            Assert.Equal(1, (await Complete(bob, other.Id)).Number);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('alice',0))", gate);
            await release.ExecuteNonQueryAsync(Ct);
            await Task.WhenAll(pending);
        }
        using var recovered = await alice.GetAsync("/v1/files", Ct);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }
}
