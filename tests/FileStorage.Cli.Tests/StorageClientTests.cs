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

    [Fact]
    public async Task InterruptedDownloadRemovesItsPartialFile()
    {
        using var http = Client(_ =>
        {
            var response = Download([1, 2], Chunker.Hash([1, 2]));
            response.Content = new StreamContent(new InterruptedStream());
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<IOException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), Path.Combine(directory, "partial"), Ct));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrAlreadyStoredContentDoesNotSendChunks(bool empty)
    {
        var path = Path.Combine(directory, "file.bin");
        var data = empty ? [] : new byte[3 * Chunker.MaxSize];
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer();
        if (!empty)
        {
            var chunk = new byte[Chunker.MaxSize];
            server.Stored[Chunker.Hash(chunk)] = chunk;
        }
        using var http = Client(server.Send);
        var result = await new StorageClient(http).UploadAsync(path, null, path + ".upload.json", Ct);
        Assert.Empty(server.Attempts);
        Assert.Equal(Chunker.Hash(data), result.Sha256);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(path + ".upload.json"));
    }

    [Fact]
    public async Task RepeatedChunksAreUploadedOnlyOnce()
    {
        var path = Path.Combine(directory, "repeated.bin");
        await File.WriteAllBytesAsync(path, new byte[3 * Chunker.MaxSize], Ct);
        var server = new UploadServer();
        using var http = Client(server.Send);
        await new StorageClient(http).UploadAsync(path, null, path + ".upload.json", Ct);
        Assert.Equal(3, server.Request!.Chunks.Length);
        Assert.Single(server.Stored);
        Assert.Equal(1, Assert.Single(server.Attempts).Value);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("name")]
    [InlineData("file-id")]
    public async Task ResumeStateCannotBeReusedForADifferentTarget(string changed)
    {
        var path = Path.Combine(directory, "file.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, [1], Ct);
        var server = new UploadServer { FailPut = 1 };
        using var http = Client(server.Send);
        await Assert.ThrowsAsync<HttpRequestException>(() => new StorageClient(http).UploadAsync(path, null, state, Ct));
        var saved = await File.ReadAllTextAsync(state, Ct);
        using var resumed = Client(server.Send);
        if (changed == "server") resumed.BaseAddress = new Uri("https://other.example.com/");
        if (changed == "name")
        {
            var renamed = Path.Combine(directory, "renamed.bin");
            File.Move(path, renamed);
            path = renamed;
        }
        Guid? fileId = changed == "file-id" ? Guid.NewGuid() : null;
        await Assert.ThrowsAsync<InvalidDataException>(() => new StorageClient(resumed).UploadAsync(path, fileId, state, Ct));
        Assert.Equal(saved, await File.ReadAllTextAsync(state, Ct));
        Assert.Equal(1, server.Creates);
        Assert.Equal(0, server.Completes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task InvalidResumeStateDoesNotContactTheServer(string json)
    {
        var path = Path.Combine(directory, "file.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, [1], Ct);
        await File.WriteAllTextAsync(state, json, Ct);
        var contacted = false;
        using var http = Client(_ => { contacted = true; throw new InvalidOperationException("Unexpected request."); });
        var failure = await Record.ExceptionAsync(() => new StorageClient(http).UploadAsync(path, null, state, Ct));
        if (json == "null") Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<JsonException>(failure);
        Assert.False(contacted);
        Assert.Equal(json, await File.ReadAllTextAsync(state, Ct));
    }

    [Fact]
    public async Task AbortedUploadKeepsStateAndDoesNotSendContent()
    {
        var path = Path.Combine(directory, "file.bin");
        await File.WriteAllBytesAsync(path, [1], Ct);
        var calls = 0;
        using var http = Client(message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            var status = new UploadStatus(Guid.NewGuid(), Guid.NewGuid(), "aborted", DateTimeOffset.UtcNow, [], null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(status) });
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StorageClient(http).UploadAsync(path, null, path + ".upload.json", Ct));
        Assert.Equal(1, calls);
        Assert.True(File.Exists(path + ".upload.json"));
    }

    [Fact]
    public async Task UnknownMissingChunkPreventsCompletion()
    {
        var path = Path.Combine(directory, "file.bin");
        await File.WriteAllBytesAsync(path, [1], Ct);
        var calls = 0;
        using var http = Client(message =>
        {
            calls++;
            Assert.Equal("/v1/uploads", message.RequestUri!.AbsolutePath);
            var status = new UploadStatus(Guid.NewGuid(), Guid.NewGuid(), "pending", DateTimeOffset.UtcNow.AddHours(1), [Chunker.Hash([2])], null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(status) });
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => new StorageClient(http).UploadAsync(path, null, path + ".upload.json", Ct));
        Assert.Equal(1, calls);
        Assert.True(File.Exists(path + ".upload.json"));
    }

    [Fact]
    public async Task ExpiredSessionPreservesResumeStateForInspection()
    {
        var path = Path.Combine(directory, "file.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, [1], Ct);
        using var first = Client(new UploadServer { FailPut = 1 }.Send);
        await Assert.ThrowsAsync<HttpRequestException>(() => new StorageClient(first).UploadAsync(path, null, state, Ct));
        var saved = await File.ReadAllTextAsync(state, Ct);
        using var resumed = Client(message =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone) { Content = new StringContent("expired") });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new StorageClient(resumed).UploadAsync(path, null, state, Ct));
        Assert.Equal(HttpStatusCode.Gone, error.StatusCode);
        Assert.Equal(saved, await File.ReadAllTextAsync(state, Ct));
    }

    [Fact]
    public async Task DestinationCreatedDuringDownloadIsPreserved()
    {
        var destination = Path.Combine(directory, "race.bin");
        using var http = Client(async _ =>
        {
            await File.WriteAllTextAsync(destination, "other writer", Ct);
            return Download([1], Chunker.Hash([1]));
        });
        await Assert.ThrowsAsync<IOException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), destination, Ct));
        Assert.Equal("other writer", await File.ReadAllTextAsync(destination, Ct));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task CanceledDownloadRemovesItsPartialFile()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var http = Client(_ =>
        {
            var response = Download([1, 2], Chunker.Hash([1, 2]));
            response.Content = new StreamContent(new CancelingStream(cancellation));
            return Task.FromResult(response);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), Path.Combine(directory, "canceled"), cancellation.Token));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public async Task InvalidJsonResponsesAreNotTreatedAsEmptyLists(string body)
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));
        var failure = await Record.ExceptionAsync(() => new StorageClient(http).GetAsync<FileEntry[]>("v1/files", Ct));
        if (body == "null") Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<JsonException>(failure);
    }

    [Fact]
    public async Task FailedCompletionResumesWithoutResendingStoredChunks()
    {
        var path = Path.Combine(directory, "retry.bin");
        var state = path + ".upload.json";
        byte[] data = [10, 20, 30];
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer();
        var reject = true;
        using var http = Client(message =>
        {
            if (reject && message.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
            {
                reject = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("retry completion")
                });
            }
            return server.Send(message);
        });
        var client = new StorageClient(http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.True(File.Exists(state));
        Assert.Equal(0, server.Completes);
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.Equal(1, Assert.Single(server.Attempts).Value);
        Assert.False(File.Exists(state));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("chunk")]
    [InlineData("complete")]
    public async Task CanceledUploadsKeepResumableStateAndReleaseTheStateLock(string stage)
    {
        var path = Path.Combine(directory, "cancel.bin");
        var state = path + ".upload.json";
        byte[] data = [11, 22, 33];
        await File.WriteAllBytesAsync(path, data, Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var server = new UploadServer();
        using var interrupted = Client(message =>
        {
            var matches = stage switch
            {
                "create" => message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == "/v1/uploads",
                "chunk" => message.Method == HttpMethod.Put,
                _ => message.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
            };
            if (matches)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return server.Send(message);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageClient(interrupted).UploadAsync(path, null, state, cancellation.Token));
        Assert.True(File.Exists(state));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(state, Ct));
        var key = saved.RootElement.GetProperty("key").GetString();
        Assert.False(string.IsNullOrEmpty(key));
        using var resumed = Client(server.Send);
        var version = await new StorageClient(resumed).UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(key, Assert.Single(server.Keys));
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.Equal(1, Assert.Single(server.Attempts).Value);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task ConcurrentUploadsCannotShareAStateFile()
    {
        var path = Path.Combine(directory, "locked.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, [1, 2, 3], Ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new UploadServer();
        using var first = Client(async message =>
        {
            if (message.RequestUri!.AbsolutePath == "/v1/uploads")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Ct);
            }
            return await server.Send(message);
        });
        var running = new StorageClient(first).UploadAsync(path, null, state, Ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            var before = await File.ReadAllTextAsync(state, Ct);
            var requests = 0;
            using var second = Client(message => { requests++; return server.Send(message); });
            await Assert.ThrowsAsync<IOException>(() => new StorageClient(second).UploadAsync(path, null, state, Ct));
            Assert.Equal(0, requests);
            Assert.Equal(before, await File.ReadAllTextAsync(state, Ct));
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task AFailedStatusRequestPreservesStateAndCanBeRetried()
    {
        var path = Path.Combine(directory, "status.bin");
        var state = path + ".upload.json";
        await File.WriteAllBytesAsync(path, [2, 4, 6], Ct);
        var server = new UploadServer { FailPut = 1 };
        using var http = Client(server.Send);
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        var before = await File.ReadAllTextAsync(state, Ct);
        using var unavailable = Client(message =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("storage unavailable")
            });
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new StorageClient(unavailable).UploadAsync(path, null, state, Ct));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(state, Ct));
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash([2, 4, 6]), version.Sha256);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task DownloadsLargerThanTheReadBufferPreserveEveryByte()
    {
        var data = new byte[3 * 65536 + 17];
        new Random(23).NextBytes(data);
        var destination = Path.Combine(directory, "large.bin");
        using var http = Client(_ => Task.FromResult(Download(data, Chunker.Hash(data))));
        await new StorageClient(http).DownloadAsync(Guid.NewGuid(), Guid.NewGuid(), destination, Ct);
        Assert.Equal(data, await File.ReadAllBytesAsync(destination, Ct));
        Assert.Single(Directory.GetFiles(directory));
    }

    private sealed class CancelingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class InterruptedStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new IOException("Connection closed.");
            read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
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
