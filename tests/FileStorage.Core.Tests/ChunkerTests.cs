using FileStorage.Core;

namespace FileStorage.Core.Tests;

public class ChunkerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(1048576)]
    [InlineData(5000000)]
    public async Task ChunksReconstructInputAndRespectBounds(int size)
    {
        var data = Data(size);
        var chunks = await Read(data);
        Assert.Equal(data, chunks.SelectMany(c => c.Data));
        Assert.All(chunks, c =>
        {
            Assert.InRange(c.Data.Length, 1, Chunker.MaxSize);
            Assert.Equal(Chunker.Hash(c.Data), c.Hash);
        });
        Assert.All(chunks.SkipLast(1), c => Assert.True(c.Data.Length >= Chunker.MinSize));
        Assert.Equal(chunks.Select(c => c.Hash), (await Read(data)).Select(c => c.Hash));
    }

    [Fact]
    public async Task ChunkBoundariesDoNotDependOnStreamReadSize()
    {
        var data = Data(3000000);
        var normal = await Read(data);
        await using var shortReads = new ShortReadStream(data);
        var actual = new List<string>();
        await foreach (var chunk in Chunker.ReadAsync(shortReads, TestContext.Current.CancellationToken)) actual.Add(chunk.Hash);
        Assert.Equal(normal.Select(c => c.Hash), actual);
    }

    [Fact]
    public async Task InsertionNearStartRetainsMostChunks()
    {
        var original = Data(12 * 1024 * 1024);
        var modified = original[..1000].Concat(Data(321)).Concat(original[1000..]).ToArray();
        var old = await Read(original);
        var updated = await Read(modified);
        var known = old.Select(c => c.Hash).ToHashSet();
        var reused = updated.Where(c => known.Contains(c.Hash)).Sum(c => (long)c.Data.Length);
        Assert.True(reused > original.Length * 0.8, $"Only {reused} bytes were reused.");
        Assert.Equal(modified, updated.SelectMany(c => c.Data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task RepetitiveInputStaysBounded(byte value)
    {
        var data = Enumerable.Repeat(value, 3 * Chunker.MaxSize + 11).ToArray();
        var chunks = await Read(data);
        Assert.Equal(data, chunks.SelectMany(c => c.Data));
        Assert.All(chunks, c => Assert.InRange(c.Data.Length, 1, Chunker.MaxSize));
    }

    [Fact]
    public async Task CancellationStopsReading()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Chunker.ReadAsync(new MemoryStream(Data(100)), canceled.Token)) { }
        });
    }

    [Fact]
    public void HashMatchesKnownVector() => Assert.Equal(
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Chunker.Hash("abc"u8));

    private static byte[] Data(int size)
    {
        var bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static async Task<List<Chunk>> Read(byte[] data)
    {
        var result = new List<Chunk>();
        await foreach (var chunk in Chunker.ReadAsync(new MemoryStream(data), TestContext.Current.CancellationToken)) result.Add(chunk);
        return result;
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 127)], cancellationToken);
    }
}
