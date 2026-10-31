using System.Net;
using System.Net.Http.Json;
using Amazon.S3.Model;
using FileStorage.Core;

namespace FileStorage.Api.Tests;

public sealed partial class UploadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalSessionUpdateFailureRollsBackTheFileAndVersion(bool existingFile)
    {
        using var client = fixture.Client();
        FileVersion? original = null;
        if (existingFile)
        {
            var (first, _) = await Create(client, [71], name: "original.bin");
            await SendMissing(client, first, [71]);
            original = await Complete(client, first.Id);
        }
        var before = await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct);
        var (upload, _) = await Create(client, [72], original?.FileId, "replacement.bin");
        await SendMissing(client, upload, [72]);
        await fixture.SqlAsync("ALTER TABLE uploads ADD CONSTRAINT reject_completion CHECK (state <> 'completed') NOT VALID");
        try
        {
            using var failed = await client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal(before, await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct));
            var pending = await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct);
            Assert.Equal("pending", pending!.State);
            Assert.Empty(pending.Missing);
            Assert.Null(pending.Version);
            if (original is not null)
            {
                Assert.Equal(original, Assert.Single((await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{original.FileId}/versions", Ct))!));
                Assert.Equal(new byte[] { 71 }, await client.GetByteArrayAsync(Content(original), Ct));
            }
        }
        finally { await fixture.SqlAsync("ALTER TABLE uploads DROP CONSTRAINT reject_completion"); }
        var version = await Complete(client, upload.Id);
        Assert.Equal(existingFile ? 2 : 1, version.Number);
        Assert.Equal("replacement.bin", version.Name);
        Assert.Equal(version, await Complete(client, upload.Id));
        Assert.Equal(new byte[] { 72 }, await client.GetByteArrayAsync(Content(version), Ct));
        var history = (await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{upload.FileId}/versions", Ct))!;
        Assert.Equal(existingFile ? 2 : 1, history.Length);
        Assert.Equal(version, history[0]);
    }

    [Fact]
    public async Task AbortAndCompletionCannotBothSucceed()
    {
        using var client = fixture.Client();
        var (upload, _) = await Create(client, [81]);
        await SendMissing(client, upload, [81]);
        var complete = client.PostAsync($"/v1/uploads/{upload.Id}/complete", null, Ct);
        var abort = client.DeleteAsync($"/v1/uploads/{upload.Id}", Ct);
        await Task.WhenAll(complete, abort);
        using var completed = await complete;
        using var aborted = await abort;
        var status = (await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!;
        if (completed.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.Conflict, aborted.StatusCode);
            Assert.Equal("completed", status.State);
            var version = (await completed.Content.ReadFromJsonAsync<FileVersion>(Ct))!;
            Assert.Equal(version, status.Version);
            Assert.Equal(version, await Complete(client, upload.Id));
            Assert.Single((await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{upload.FileId}/versions", Ct))!);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, completed.StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, aborted.StatusCode);
            Assert.Equal("aborted", status.State);
            Assert.Null(status.Version);
            Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        }
    }

    [Fact]
    public async Task ConflictingConcurrentCreatesReserveTheKeyForOnlyOneRequest()
    {
        using var client = fixture.Client();
        var request = await Manifest.CreateAsync(Stream.Null, "first.bin", cancellationToken: Ct);
        using var first = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request) };
        using var second = new HttpRequestMessage(HttpMethod.Post, "/v1/uploads") { Content = JsonContent.Create(request with { Name = "second.bin" }) };
        first.Headers.Add("Idempotency-Key", "competing-key");
        second.Headers.Add("Idempotency-Key", "competing-key");
        var pendingFirst = client.SendAsync(first, Ct);
        var pendingSecond = client.SendAsync(second, Ct);
        await Task.WhenAll(pendingFirst, pendingSecond);
        using var firstResponse = await pendingFirst;
        using var secondResponse = await pendingSecond;
        var firstWon = firstResponse.StatusCode == HttpStatusCode.Created;
        var winner = firstWon ? firstResponse : secondResponse;
        var loser = firstWon ? secondResponse : firstResponse;
        Assert.Equal(HttpStatusCode.Created, winner.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        var upload = (await winner.Content.ReadFromJsonAsync<UploadStatus>(Ct))!;
        var winningRequest = firstWon ? request : request with { Name = "second.bin" };
        Assert.Equal(upload.Id, (await Create(client, winningRequest, "competing-key")).Id);
        var version = await Complete(client, upload.Id);
        Assert.Equal(winningRequest.Name, version.Name);
        Assert.Single((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
    }

    [Fact]
    public async Task PendingSessionsShareNewChunksWithoutSharingTheirLifecycle()
    {
        using var client = fixture.Client();
        byte[] data = [91, 92, 93];
        var (first, request) = await Create(client, data);
        var second = await Create(client, request);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.FileId, second.FileId);
        Assert.Equal(first.Missing, second.Missing);
        await SendMissing(client, first, data);
        var ready = (await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{second.Id}", Ct))!;
        Assert.Empty(ready.Missing);
        using var abort = await client.DeleteAsync($"/v1/uploads/{first.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
        var version = await Complete(client, second.Id);
        Assert.Equal(second.FileId, version.FileId);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
        Assert.Equal(second.FileId, Assert.Single((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!).Id);
        Assert.Equal("aborted", (await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{first.Id}", Ct))!.State);
    }

    [Fact]
    public async Task ExpiredSessionsCanBeAbortedWithoutCreatingAVersion()
    {
        using var client = fixture.Client();
        var (upload, request) = await Create(client, [101], key: "expired-abort");
        await fixture.SqlAsync("UPDATE uploads SET expires_at=clock_timestamp()-interval '1 second' WHERE id=$1", upload.Id);
        using var abort = await client.DeleteAsync($"/v1/uploads/{upload.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, abort.StatusCode);
        var status = await Create(client, request, "expired-abort");
        Assert.Equal(upload.Id, status.Id);
        Assert.Equal("aborted", status.State);
        Assert.Empty(status.Missing);
        Assert.Null(status.Version);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
    }

    [Fact]
    public async Task StoredSizeConflictsDoNotOverwriteAnExistingChunk()
    {
        using var client = fixture.Client();
        byte[] data = [111, 112, 113];
        var (upload, request) = await Create(client, data);
        await SendMissing(client, upload, data);
        await fixture.SqlAsync("UPDATE chunks SET size=4 WHERE owner='alice' AND hash=$1", request.Sha256);
        using var failed = await client.PutAsync($"/v1/uploads/{upload.Id}/chunks/{request.Sha256}", new ByteArrayContent(data), Ct);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(request.Sha256, Assert.Single((await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!.Missing));
        using (var stored = await fixture.S3.GetObjectAsync(fixture.Bucket, $"chunks/alice/{request.Sha256}", Ct))
        {
            using var bytes = new MemoryStream();
            await stored.ResponseStream.CopyToAsync(bytes, Ct);
            Assert.Equal(data, bytes.ToArray());
        }
        await fixture.SqlAsync("UPDATE chunks SET size=3 WHERE owner='alice' AND hash=$1", request.Sha256);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(await Complete(client, upload.Id)), Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedLaterChunkInterruptsTheDownloadAndCanBeRepaired(bool corrupt)
    {
        using var client = fixture.Client();
        byte[] first = [121, 122], second = [123, 124];
        var data = first.Concat(second).ToArray();
        var hash = Chunker.Hash(second);
        var request = new UploadRequest(null, "streamed.bin", data.Length, Chunker.Hash(data),
            [new(Chunker.Hash(first), first.Length), new(hash, second.Length)]);
        var upload = await Create(client, request);
        await Put(client, upload.Id, new(Chunker.Hash(first), first));
        await Put(client, upload.Id, new(hash, second));
        var version = await Complete(client, upload.Id);
        var key = $"chunks/alice/{hash}";
        if (corrupt)
            await fixture.S3.PutObjectAsync(new PutObjectRequest { BucketName = fixture.Bucket, Key = key, ContentBody = "xx" }, Ct);
        else
            await fixture.S3.DeleteObjectAsync(fixture.Bucket, key, Ct);
        var failure = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync(Content(version), HttpCompletionOption.ResponseHeadersRead, Ct);
            await response.Content.ReadAsByteArrayAsync(Ct);
        });
        Assert.True(failure is IOException or HttpRequestException, $"Expected an interrupted response, received {failure?.GetType().Name ?? "no error"}.");
        using var restored = new MemoryStream(second);
        await fixture.S3.PutObjectAsync(new PutObjectRequest { BucketName = fixture.Bucket, Key = key, InputStream = restored }, Ct);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
        Assert.Equal(version, await Complete(client, upload.Id));
    }
}
