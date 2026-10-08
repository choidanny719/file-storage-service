using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileStorage.Core;

namespace FileStorage.Cli.Tests;

public sealed class StorageClientTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public StorageClientTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsAreVerifiedBeforeDestinationAppears(bool empty)
    {
        byte[] data = empty ? [] : [1, 2, 3, 4];
        using var http = Client(_ => Task.FromResult(Download(data, Chunker.Hash(data))));
        var path = Path.Combine(directory, "download.bin");
        await new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), path, Ct);
        Assert.Equal(data, await File.ReadAllBytesAsync(path, Ct));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("mismatch")]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task BadChecksumsLeaveNoOutput(string scenario)
    {
        using var http = Client(_ => Task.FromResult(Download([1], scenario switch
        {
            "mismatch" => Chunker.Hash([2]), "missing" => null, _ => "bad"
        })));
        var path = Path.Combine(directory, "download.bin");
        await Assert.ThrowsAsync<InvalidDataException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), path, Ct));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task DownloadDoesNotOverwriteExistingFiles()
    {
        var called = false;
        using var http = Client(_ => { called = true; return Task.FromResult(Download([1], Chunker.Hash([1]))); });
        var path = Path.Combine(directory, "existing");
        await File.WriteAllTextAsync(path, "keep", Ct);
        await Assert.ThrowsAsync<IOException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), path, Ct));
        Assert.False(called);
        Assert.Equal("keep", await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task HttpErrorsDoNotCreateDestinationFiles()
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("missing") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), Path.Combine(directory, "file"), Ct));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task InterruptedUploadResumesOnlyMissingChunks()
    {
        var data = new byte[2 * Chunker.MaxSize + 3];
        new Random(7).NextBytes(data);
        var path = Path.Combine(directory, "file.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer { FailPut = 2 };
        using var http = Client(server.Send);
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        Assert.True(File.Exists(state));
        Assert.Single(server.Stored);
        var firstHash = server.Stored.Keys.Single();
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.False(File.Exists(state));
        Assert.Equal(1, server.Attempts[firstHash]);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.Equal(data, server.Request!.Chunks.SelectMany(c => server.Stored[c.Hash]));
    }

    [Fact]
    public async Task LostCreateResponseReusesTheSavedIdempotencyKey()
    {
        var path = Path.Combine(directory, "file.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        var server = new UploadServer { FailCreateResponse = true };
        using var http = Client(server.Send);
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, path + ".upload.json", Ct));
        await client.UploadAsync(path, null, path + ".upload.json", Ct);
        Assert.Equal(2, server.Keys.Count);
        Assert.Single(server.Keys.Distinct());
        Assert.Equal(1, server.Completes);
    }

    [Fact]
    public async Task ChangedFileCannotReuseAnInterruptedSession()
    {
        var path = Path.Combine(directory, "file.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        var server = new UploadServer { FailPut = 1 };
        using var http = Client(server.Send);
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, path + ".upload.json", Ct));
        await File.WriteAllBytesAsync(path, [4, 5, 6], Ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadAsync(path, null, path + ".upload.json", Ct));
        Assert.Equal(0, server.Completes);
        Assert.Equal(1, server.Creates);
    }

    [Fact]
    public async Task AlreadyCompletedSessionReturnsItsOriginalVersion()
    {
        var path = Path.Combine(directory, "file.bin");
        await File.WriteAllBytesAsync(path, [], Ct);
        var server = new UploadServer { FailCompleteResponse = true };
        using var http = Client(server.Send);
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, path + ".upload.json", Ct));
        var version = await client.UploadAsync(path, null, path + ".upload.json", Ct);
        Assert.Equal(server.Version, version);
        Assert.Equal(1, server.Completes);
    }

    private static HttpResponseMessage Download(byte[] data, string? checksum)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        if (checksum is not null) response.Headers.Add("X-Content-SHA256", checksum);
        return response;
    }

    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Handler(send)) { BaseAddress = new Uri("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class UploadServer
    {
        private readonly Guid id = Guid.NewGuid();
        private readonly Guid fileId = Guid.NewGuid();
        public UploadRequest? Request { get; private set; }
        public FileVersion? Version { get; private set; }
        public Dictionary<string, byte[]> Stored { get; } = [];
        public Dictionary<string, int> Attempts { get; } = [];
        public List<string> Keys { get; } = [];
        public int FailPut { get; init; }
        public bool FailCreateResponse { get; init; }
        public bool FailCompleteResponse { get; init; }
        public int Creates { get; private set; }
        public int Completes { get; private set; }
        private int puts;

        public async Task<HttpResponseMessage> Send(HttpRequestMessage message)
        {
            var ct = TestContext.Current.CancellationToken;
            if (message.Method == HttpMethod.Put)
            {
                var hash = message.RequestUri!.Segments.Last();
                Attempts[hash] = Attempts.GetValueOrDefault(hash) + 1;
                if (++puts == FailPut) throw new HttpRequestException("Interrupted.");
                Stored[hash] = await message.Content!.ReadAsByteArrayAsync(ct);
                return new(HttpStatusCode.NoContent);
            }
            if (message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                Completes++;
                Version = new(Guid.NewGuid(), fileId, 1, Request!.Name, Request.Size, Request.Sha256, DateTimeOffset.UtcNow);
                if (FailCompleteResponse) throw new HttpRequestException("Lost completion response.");
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Version) };
            }
            if (message.Method == HttpMethod.Post)
            {
                Creates++;
                Keys.Add(message.Headers.GetValues("Idempotency-Key").Single());
                Request = await message.Content!.ReadFromJsonAsync<UploadRequest>(ct);
                if (FailCreateResponse && Creates == 1) throw new HttpRequestException("Lost create response.");
            }
            var status = new UploadStatus(id, fileId, Version is null ? "pending" : "completed", DateTimeOffset.UtcNow.AddDays(1),
                Request!.Chunks.Select(c => c.Hash).Where(h => !Stored.ContainsKey(h)).Distinct().ToArray(), Version);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(status) };
        }
    }
}
