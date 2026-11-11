using System.Net;
using FileStorage.Core;

namespace FileStorage.Cli.Tests;

public sealed partial class StorageClientTests
{
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
}
