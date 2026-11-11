using System.Net;
using System.Text;
using System.Text.Json;
using FileStorage.Core;

namespace FileStorage.Cli.Tests;

public sealed partial class StorageClientTests
{
    [Fact]
    public async Task LostChunkResponseResumesWithoutResendingStoredContent()
    {
        var path = Path.Combine(directory, "acknowledged.bin");
        var state = path + ".upload.json";
        byte[] data = [17, 34, 51];
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer();
        var loseResponse = true;
        using var http = Client(async message =>
        {
            var response = await server.Send(message);
            if (message.Method == HttpMethod.Put && loseResponse)
            {
                loseResponse = false;
                response.Dispose();
                throw new HttpRequestException("Lost chunk response.");
            }
            return response;
        });
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        Assert.Equal(data, Assert.Single(server.Stored).Value);
        Assert.Equal(0, server.Completes);
        Assert.True(File.Exists(state));
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(1, Assert.Single(server.Attempts).Value);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }

    [Theory]
    [InlineData("create", "null")]
    [InlineData("create", "{broken")]
    [InlineData("status", "null")]
    [InlineData("status", "{broken")]
    [InlineData("complete", "null")]
    [InlineData("complete", "{broken")]
    public async Task InvalidUploadResponsesPreserveStateForRetry(string stage, string body)
    {
        var path = Path.Combine(directory, "response.bin");
        var state = path + ".upload.json";
        byte[] data = [21, 42, 63];
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer { FailPut = stage == "status" ? 1 : 0 };
        var rejectResponse = true;
        using var http = Client(async message =>
        {
            var response = await server.Send(message);
            var matches = stage switch
            {
                "create" => message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == "/v1/uploads",
                "status" => message.Method == HttpMethod.Get,
                _ => message.RequestUri!.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal)
            };
            if (!matches || !rejectResponse) return response;
            rejectResponse = false;
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        var client = new StorageClient(http);
        if (stage == "status")
            await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        var failure = await Record.ExceptionAsync(() => client.UploadAsync(path, null, state, Ct));
        if (body == "null") Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<JsonException>(failure);
        Assert.True(File.Exists(state));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(state, Ct));
        var key = saved.RootElement.GetProperty("key").GetString();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(stage == "create", saved.RootElement.GetProperty("uploadId").ValueKind == JsonValueKind.Null);
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(server.Version, version);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(key, Assert.Single(server.Keys.Distinct()));
        Assert.Equal(stage == "create" ? 2 : 1, server.Creates);
        Assert.Equal(stage == "status" ? 2 : 1, Assert.Single(server.Attempts).Value);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task MissingSourcePreservesTheSessionUntilTheFileIsRestored()
    {
        var path = Path.Combine(directory, "restore.bin");
        var state = path + ".upload.json";
        byte[] data = [31, 62, 93];
        await File.WriteAllBytesAsync(path, data, Ct);
        var server = new UploadServer { FailPut = 1 };
        var requests = 0;
        using var http = Client(message => { requests++; return server.Send(message); });
        var client = new StorageClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.UploadAsync(path, null, state, Ct));
        var saved = await File.ReadAllTextAsync(state, Ct);
        var before = requests;
        File.Delete(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.UploadAsync(path, null, state, Ct));
        Assert.Equal(before, requests);
        Assert.Equal(saved, await File.ReadAllTextAsync(state, Ct));
        await File.WriteAllBytesAsync(path, data, Ct);
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }

    [Fact]
    public async Task UnwritableStateDestinationDoesNotCreateAnUpload()
    {
        var path = Path.Combine(directory, "source.bin");
        var state = Path.Combine(directory, "state");
        byte[] data = [41, 82, 123];
        await File.WriteAllBytesAsync(path, data, Ct);
        Directory.CreateDirectory(state);
        var requests = 0;
        var server = new UploadServer();
        using var http = Client(message => { requests++; return server.Send(message); });
        var client = new StorageClient(http);
        await Assert.ThrowsAnyAsync<IOException>(() => client.UploadAsync(path, null, state, Ct));
        Assert.Equal(0, requests);
        Assert.True(Directory.Exists(state));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        Assert.Equal(data, await File.ReadAllBytesAsync(path, Ct));
        Directory.Delete(state);
        var version = await client.UploadAsync(path, null, state, Ct);
        Assert.Equal(Chunker.Hash(data), version.Sha256);
        Assert.Equal(1, server.Creates);
        Assert.Equal(1, server.Completes);
        Assert.False(File.Exists(state));
    }
}
