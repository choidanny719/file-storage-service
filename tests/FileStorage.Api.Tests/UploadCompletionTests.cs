using System.Net.Http.Json;
using FileStorage.Core;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FileStorage.Api.Tests;

public sealed partial class UploadTests
{
    [Fact]
    public async Task CanceledCompletionWhileWaitingForTheOwnerCanBeRetried()
    {
        using var client = fixture.Client();
        var (first, _) = await Create(client, [], name: "original.bin");
        var original = await Complete(client, first.Id);
        byte[] data = [131, 132];
        var (upload, _) = await Create(client, data, first.FileId, "replacement.bin");
        await SendMissing(client, upload, data);
        var before = await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct);
        var service = fixture.App.Services.GetRequiredService<UploadService>();
        await using var gate = new NpgsqlConnection(fixture.ConnectionString);
        await gate.OpenAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('alice',0))", gate))
            await hold.ExecuteNonQueryAsync(Ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var completion = Record.ExceptionAsync(() => service.CompleteAsync("alice", upload.Id, cancellation.Token)).AsTask();
        try
        {
            await WaitForOwnerLock(gate);
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await completion.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        }
        finally
        {
            cancellation.Cancel();
            await gate.CloseAsync();
            await completion;
        }
        Assert.Equal(before, await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct));
        Assert.Equal(original, Assert.Single((await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{first.FileId}/versions", Ct))!));
        var pending = (await client.GetFromJsonAsync<UploadStatus>($"/v1/uploads/{upload.Id}", Ct))!;
        Assert.Equal("pending", pending.State);
        Assert.Null(pending.Version);
        Assert.Empty(pending.Missing);

        var version = await Complete(client, upload.Id);
        Assert.Equal(2, version.Number);
        Assert.Equal("replacement.bin", version.Name);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
        Assert.Equal(version, await Complete(client, upload.Id));
        Assert.Equal(2, (await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{first.FileId}/versions", Ct))!.Length);
    }

    [Fact]
    public async Task CompletionRechecksExpirationAfterAcquiringTheOwnerLock()
    {
        using var client = fixture.Client();
        byte[] data = [141, 142];
        var (upload, request) = await Create(client, data);
        await SendMissing(client, upload, data);
        var service = fixture.App.Services.GetRequiredService<UploadService>();
        await using var gate = new NpgsqlConnection(fixture.ConnectionString);
        await gate.OpenAsync(Ct);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('alice',0))", gate))
            await hold.ExecuteNonQueryAsync(Ct);
        var completion = Record.ExceptionAsync(() => service.CompleteAsync("alice", upload.Id, Ct)).AsTask();
        try
        {
            await WaitForOwnerLock(gate);
            await fixture.SqlAsync("UPDATE uploads SET expires_at=clock_timestamp()-interval '1 second' WHERE id=$1", upload.Id);
        }
        finally
        {
            await gate.CloseAsync();
            await completion;
        }
        var fault = Assert.IsType<ApiFault>(await completion);
        Assert.Equal(410, fault.Status);
        Assert.Equal("upload_expired", fault.Code);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!);
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var query = new NpgsqlCommand("SELECT state,version_id,(SELECT count(*) FROM versions) FROM uploads WHERE id=$1", connection);
            query.Parameters.AddWithValue(upload.Id);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal("pending", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(0, reader.GetInt64(2));
        }
        var replacement = await Create(client, request);
        Assert.NotEqual(upload.Id, replacement.Id);
        Assert.Empty(replacement.Missing);
        var version = await Complete(client, replacement.Id);
        Assert.Equal(1, version.Number);
        Assert.Equal(data, await client.GetByteArrayAsync(Content(version), Ct));
    }

    private async Task WaitForOwnerLock(NpgsqlConnection connection)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name=current_setting('application_name') AND wait_event='advisory'",
            connection);
        while ((long)(await command.ExecuteScalarAsync(deadline.Token))! == 0)
            await Task.Delay(10, deadline.Token);
    }
}
