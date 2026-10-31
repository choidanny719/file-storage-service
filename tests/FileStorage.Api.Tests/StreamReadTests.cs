namespace FileStorage.Api.Tests;

public sealed class StreamReadTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    [InlineData(3, 8)]
    public async Task BoundedReadsAcceptShortReadsAndStopAfterOneExcessByte(int expected, int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        await using var input = new FragmentedStream(data);
        var bytes = await ObjectStore.ReadBoundedAsync(input, expected, TestContext.Current.CancellationToken);
        Assert.Equal(data.Take(expected + 1), bytes);
        Assert.Equal(Math.Min(length, expected + 1), input.Consumed);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task BoundedReadsPropagateCancellationWithoutReadingOrClosingTheStream()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await using var input = new FragmentedStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObjectStore.ReadBoundedAsync(input, 3, cancellation.Token));
        Assert.Equal(0, input.Consumed);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task BoundedReadsDoNotReturnPartialDataAfterAnInputFailure()
    {
        await using var input = new FragmentedStream([1, 2, 3], failAfter: 1);
        var error = await Assert.ThrowsAsync<IOException>(() => ObjectStore.ReadBoundedAsync(input, 3, TestContext.Current.CancellationToken));
        Assert.Equal("Read interrupted.", error.Message);
        Assert.Equal(1, input.Consumed);
        Assert.True(input.CanRead);
    }

    private sealed class FragmentedStream(byte[] data, int failAfter = int.MaxValue) : MemoryStream(data)
    {
        public int Consumed { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Consumed >= failAfter) throw new IOException("Read interrupted.");
            var read = await base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
            Consumed += read;
            return read;
        }
    }
}
