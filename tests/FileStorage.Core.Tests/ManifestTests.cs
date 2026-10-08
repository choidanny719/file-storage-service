using FileStorage.Core;

namespace FileStorage.Core.Tests;

public class ManifestTests
{
    private static readonly string Hash = Chunker.Hash([1]);
    private static UploadRequest Valid() => new(null, "file.bin", 1, Hash, [new(Hash, 1)]);

    [Fact]
    public async Task ManifestContainsFullChecksumAndOrderedChunks()
    {
        var data = new byte[2 * Chunker.MaxSize + 7];
        new Random(7).NextBytes(data);
        var manifest = await Manifest.CreateAsync(new MemoryStream(data), "file.bin", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(Manifest.Validate(manifest));
        Assert.Equal(Chunker.Hash(data), manifest.Sha256);
        Assert.Equal(data.Length, manifest.Chunks.Sum(c => c.Size));
    }

    [Fact]
    public async Task EmptyFilesHaveAnEmptyManifest()
    {
        var request = await Manifest.CreateAsync(Stream.Null, "empty", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(request.Chunks);
        Assert.Equal(0, request.Size);
        Assert.Equal(Chunker.Hash([]), request.Sha256);
        Assert.Null(Manifest.Validate(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../file")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void UnsafeNamesAreRejected(string name) => Assert.NotNull(Manifest.Validate(Valid() with { Name = name }));

    [Fact]
    public void NameLengthBoundaryIsEnforced()
    {
        Assert.Null(Manifest.Validate(Valid() with { Name = new string('a', 200) }));
        Assert.NotNull(Manifest.Validate(Valid() with { Name = new string('a', 201) }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    public void InvalidChecksumsAreRejected(string? hash) => Assert.False(Manifest.IsHash(hash));

    [Fact]
    public void InvalidManifestShapesAreRejected()
    {
        var cases = new[] {
            Valid() with { Size = -1 }, Valid() with { Size = Manifest.MaxFileSize + 1 },
            Valid() with { FileId = Guid.Empty }, Valid() with { Chunks = null! },
            Valid() with { Chunks = [] }, Valid() with { Chunks = [new(Hash, 0)] },
            Valid() with { Chunks = [new(Hash, Chunker.MaxSize + 1)] },
            Valid() with { Chunks = [null!] }, Valid() with { Chunks = [new("bad", 1)] },
            Valid() with { Size = 3, Chunks = [new(Hash, 1), new(Hash, 2)] },
            Valid() with { Size = 0, Chunks = [] },
            Valid() with { Chunks = Enumerable.Repeat(new ChunkInfo(Hash, 1), Manifest.MaxChunks + 1).ToArray() }
        };
        Assert.All(cases, request => Assert.NotNull(Manifest.Validate(request)));
    }

    [Fact]
    public void RepeatedChunksRemainInTheOrderedManifest()
    {
        var request = Valid() with { Size = 2, Chunks = [new(Hash, 1), new(Hash, 1)] };
        Assert.Null(Manifest.Validate(request));
    }
}
