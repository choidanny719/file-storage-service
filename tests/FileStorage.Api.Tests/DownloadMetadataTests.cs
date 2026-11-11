using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;

namespace FileStorage.Api.Tests;

public sealed partial class UploadTests
{
    [Theory]
    [InlineData("résumé 数据.bin")]
    [InlineData("report \"draft\"; 100% #1.bin")]
    public async Task DownloadHeadersDescribeTheRequestedVersionAfterARename(string name)
    {
        using var client = fixture.Client();
        byte[] data = [13, 26, 39];
        var (upload, _) = await Create(client, data, name: name);
        await SendMissing(client, upload, data);
        var original = await Complete(client, upload.Id);
        var (replacement, _) = await Create(client, [52], original.FileId, "replacement.bin");
        await SendMissing(client, replacement, [52]);
        var latest = await Complete(client, replacement.Id);
        foreach (var version in new[] { original, latest })
        {
            using var response = await client.GetAsync(Content(version), Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(version.Size, response.Content.Headers.ContentLength);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal(version.Name, response.Content.Headers.ContentDisposition.FileNameStar);
            Assert.Equal(version.Sha256, Assert.Single(response.Headers.GetValues("X-Content-SHA256")));
            Assert.Equal('"' + version.Sha256 + '"', response.Headers.ETag!.Tag);
            Assert.False(response.Headers.ETag.IsWeak);
            Assert.Equal(version == original ? data : new byte[] { 52 }, await response.Content.ReadAsByteArrayAsync(Ct));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstChunkFailureReturnsJsonWithoutDownloadHeaders(bool corrupt)
    {
        using var client = fixture.Client();
        byte[] data = [14, 28, 42];
        var (upload, request) = await Create(client, data);
        await SendMissing(client, upload, data);
        var version = await Complete(client, upload.Id);
        var key = $"chunks/alice/{request.Sha256}";
        if (corrupt)
            await fixture.S3.PutObjectAsync(new PutObjectRequest { BucketName = fixture.Bucket, Key = key, ContentBody = "bad" }, Ct);
        else
            await fixture.S3.DeleteObjectAsync(fixture.Bucket, key, Ct);
        using var response = await client.GetAsync(Content(version), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Null(response.Headers.ETag);
        Assert.False(response.Headers.Contains("X-Content-SHA256"));
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(corrupt ? "storage_integrity_error" : "storage_unavailable", error.GetProperty("error").GetString());
        using var restored = new MemoryStream(data);
        await fixture.S3.PutObjectAsync(new PutObjectRequest { BucketName = fixture.Bucket, Key = key, InputStream = restored }, Ct);
        using var retry = await client.GetAsync(Content(version), Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(data, await retry.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(version.Name, retry.Content.Headers.ContentDisposition!.FileNameStar);
        Assert.Equal(version.Sha256, Assert.Single(retry.Headers.GetValues("X-Content-SHA256")));
    }
}
