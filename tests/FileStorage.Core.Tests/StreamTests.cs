using FileStorage.Core;

namespace FileStorage.Core.Tests;

public sealed class StreamTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NonSeekableInputProducesTheSameManifest()
    {
        var data = new byte[1500000];
        new Random(13).NextBytes(data);
        await using var normal = new MemoryStream(data);
        await using var fragmented = new ForwardStream(data);
        var fileId = Guid.NewGuid();
        var expected = await Manifest.CreateAsync(normal, "자료.bin", fileId, Ct);
        var actual = await Manifest.CreateAsync(fragmented, "자료.bin", fileId, Ct);
        Assert.Equal(expected.FileId, actual.FileId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.Chunks, actual.Chunks);
    }

    [Fact]
    public async Task ReadingStartsAtTheCurrentPositionAndLeavesInputOpen()
    {
        await using var input = new MemoryStream([9, 8, 1, 2, 3]);
        input.Position = 2;
        var manifest = await Manifest.CreateAsync(input, "tail", cancellationToken: Ct);
        Assert.Equal(3, manifest.Size);
        Assert.Equal(Chunker.Hash([1, 2, 3]), manifest.Sha256);
        Assert.True(input.CanRead);
        Assert.Equal(input.Length, input.Position);
    }

    [Theory]
    [InlineData(1048576, 1)]
    [InlineData(1048577, 2)]
    [InlineData(2097152, 2)]
    [InlineData(2097153, 3)]
    public async Task ExactBoundariesDoNotCreateEmptyTrailingChunks(int size, int count)
    {
        await using var input = new MemoryStream(new byte[size]);
        var chunks = new List<Chunk>();
        await foreach (var chunk in Chunker.ReadAsync(input, Ct)) chunks.Add(chunk);
        Assert.Equal(count, chunks.Count);
        Assert.Equal(size, chunks.Sum(c => c.Data.Length));
        Assert.All(chunks, chunk => Assert.NotEmpty(chunk.Data));
        for (var i = 1; i < chunks.Count; i++) Assert.NotSame(chunks[0].Data, chunks[i].Data);
    }

    [Fact]
    public async Task CancelingAfterAChunkStopsFurtherReads()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var input = new MemoryStream(new byte[3 * Chunker.MaxSize]);
        await using var chunks = Chunker.ReadAsync(input, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await chunks.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await chunks.MoveNextAsync());
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task StoppingEnumerationDoesNotCloseTheCallersStream()
    {
        await using var input = new MemoryStream(new byte[3 * Chunker.MaxSize]);
        await using (var chunks = Chunker.ReadAsync(input, Ct).GetAsyncEnumerator(Ct))
            Assert.True(await chunks.MoveNextAsync());
        Assert.True(input.CanRead);
        Assert.True(input.Position < input.Length);
    }

    [Fact]
    public async Task ReadFailureDoesNotProduceATruncatedManifest()
    {
        await using var input = new ForwardStream(new byte[1000], failAfter: 2);
        var error = await Assert.ThrowsAsync<IOException>(() => Manifest.CreateAsync(input, "broken", cancellationToken: Ct));
        Assert.Equal("Input failed.", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bad\0name")]
    [InlineData("bad\tname")]
    public async Task ManifestCreationRejectsInvalidNames(string? name)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Manifest.CreateAsync(Stream.Null, name!, cancellationToken: Ct));
    }

    private sealed class ForwardStream(byte[] data, int failAfter = int.MaxValue) : Stream
    {
        private int offset;
        private int reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++reads > failAfter) throw new IOException("Input failed.");
            var count = Math.Min(Math.Min(buffer.Length, 97), data.Length - offset);
            data.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
