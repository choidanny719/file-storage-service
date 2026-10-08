using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace FileStorage.Core;

public sealed record Chunk(string Hash, byte[] Data);

public static class Chunker
{
    public const int MinSize = 64 * 1024;
    public const int AverageSize = 256 * 1024;
    public const int MaxSize = 1024 * 1024;
    private const int WindowSize = 64;
    private static readonly ulong[] Table = CreateTable();

    public static async IAsyncEnumerable<Chunk> ReadAsync(Stream input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new byte[64 * 1024];
        var chunk = new byte[MaxSize];
        var window = new byte[WindowSize];
        int length = 0, position = 0, filled = 0;
        ulong rolling = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                rolling = BitOperations.RotateLeft(rolling, 1) ^ Table[value];
                if (filled == WindowSize)
                    rolling ^= BitOperations.RotateLeft(Table[window[position]], WindowSize);
                else
                    filled++;
                window[position] = value;
                position = (position + 1) % WindowSize;
                chunk[length++] = value;
                if (length == MaxSize || (length >= MinSize && (rolling & (AverageSize - 1)) == 0))
                {
                    var data = chunk.AsSpan(0, length).ToArray();
                    yield return new Chunk(Hash(data), data);
                    length = position = filled = 0;
                    rolling = 0;
                }
            }
        }
        if (length > 0)
        {
            var data = chunk.AsSpan(0, length).ToArray();
            yield return new Chunk(Hash(data), data);
        }
    }

    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static ulong[] CreateTable()
    {
        var table = new ulong[256];
        ulong state = 0x6a09e667f3bcc909;
        for (var i = 0; i < table.Length; i++)
        {
            state = unchecked(state + 0x9e3779b97f4a7c15);
            var value = state;
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111eb);
            table[i] = value ^ (value >> 31);
        }
        return table;
    }
}
