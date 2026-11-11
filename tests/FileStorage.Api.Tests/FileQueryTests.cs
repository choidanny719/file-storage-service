using System.Net;
using System.Net.Http.Json;
using FileStorage.Core;

namespace FileStorage.Api.Tests;

public sealed partial class UploadTests
{
    [Fact]
    public async Task VersionCursorPagesRemainStableWhenNewVersionsArrive()
    {
        using var client = fixture.Client();
        var versions = new List<FileVersion>();
        Guid? fileId = null;
        for (var number = 1; number <= 5; number++)
        {
            var (upload, _) = await Create(client, [], fileId, $"version-{number}.bin");
            var version = await Complete(client, upload.Id);
            versions.Add(version);
            fileId = version.FileId;
        }
        var route = $"/v1/files/{fileId}/versions";
        var first = (await client.GetFromJsonAsync<FileVersion[]>(route + "?limit=2", Ct))!;
        Assert.Equal(new[] { versions[4], versions[3] }, first);
        var (newUpload, _) = await Create(client, [], fileId, "version-6.bin");
        var latest = await Complete(client, newUpload.Id);
        var collected = first.ToList();
        var before = first[^1].Number;
        for (var pageNumber = 0; pageNumber < 3; pageNumber++)
        {
            var page = (await client.GetFromJsonAsync<FileVersion[]>($"{route}?before={before}&limit=2", Ct))!;
            if (page.Length == 0) break;
            Assert.All(page, version => Assert.True(version.Number < before));
            collected.AddRange(page);
            before = page[^1].Number;
        }
        Assert.Equal(versions.AsEnumerable().Reverse(), collected);
        Assert.Equal(5, collected.Select(version => version.Id).Distinct().Count());
        Assert.Empty((await client.GetFromJsonAsync<FileVersion[]>($"{route}?before={before}&limit=2", Ct))!);
        Assert.Equal(versions.Append(latest).Reverse(), (await client.GetFromJsonAsync<FileVersion[]>(route, Ct))!);
    }

    [Fact]
    public async Task FileListingChangesOnlyWhenANewVersionCompletes()
    {
        using var client = fixture.Client();
        var (first, _) = await Create(client, [], name: "original.bin");
        var original = await Complete(client, first.Id);
        var (second, _) = await Create(client, [], name: "other.bin");
        await Complete(client, second.Id);
        await fixture.SqlAsync("UPDATE files SET updated_at='2020-01-01T00:00:00Z' WHERE id=$1", first.FileId);
        await fixture.SqlAsync("UPDATE files SET updated_at='2021-01-01T00:00:00Z' WHERE id=$1", second.FileId);
        var before = (await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!;
        Assert.Equal(new[] { second.FileId, first.FileId }, before.Select(file => file.Id));
        var (abandoned, _) = await Create(client, [], first.FileId, "abandoned.bin");
        Assert.Equal(before, await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct));
        using var aborted = await client.DeleteAsync($"/v1/uploads/{abandoned.Id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, aborted.StatusCode);
        Assert.Equal(before, await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct));
        var (replacement, _) = await Create(client, [], first.FileId, "renamed.bin");
        var latest = await Complete(client, replacement.Id);
        var after = (await client.GetFromJsonAsync<FileEntry[]>("/v1/files", Ct))!;
        Assert.Equal(new[] { first.FileId, second.FileId }, after.Select(file => file.Id));
        Assert.Equal("renamed.bin", after[0].Name);
        Assert.Equal(2, after[0].Versions);
        Assert.True(after[0].UpdatedAt > before[1].UpdatedAt);
        Assert.Equal(before[0], after[1]);
        Assert.Equal(new[] { latest, original }, (await client.GetFromJsonAsync<FileVersion[]>($"/v1/files/{first.FileId}/versions", Ct))!);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    public async Task FilePagesApplyOwnerFilteringBeforeLimitsAndOffsets(int? limit, int expected)
    {
        foreach (var owner in new[] { "alice", "bob" })
        {
            await fixture.SqlAsync("""
                WITH created AS (
                    INSERT INTO files (id, owner, name, updated_at)
                    SELECT gen_random_uuid(), $1, $1 || '-' || n,
                        '2020-01-01T00:00:00Z'::timestamptz + n * interval '1 second'
                        + CASE WHEN $1 = 'bob' THEN interval '1 day' ELSE interval '0 days' END
                    FROM generate_series(1, 101) AS n
                    RETURNING id, name
                )
                INSERT INTO versions (id, file_id, number, name, size, sha256, manifest)
                SELECT gen_random_uuid(), id, 1, name, 0, $2, '[]'::jsonb FROM created
                """, owner, Chunker.Hash([]));
        }
        using var client = fixture.Client();
        var first = (await client.GetFromJsonAsync<FileEntry[]>("/v1/files" + (limit is null ? "" : $"?limit={limit}"), Ct))!;
        Assert.Equal(expected, first.Length);
        Assert.Equal(Enumerable.Range(1, 101).Reverse().Take(expected).Select(n => $"alice-{n}"), first.Select(file => file.Name));
        Assert.All(first, file => Assert.Equal(1, file.Versions));
        var remaining = (await client.GetFromJsonAsync<FileEntry[]>($"/v1/files?offset={expected}&limit=100", Ct))!;
        Assert.Equal(Enumerable.Range(1, 101 - expected).Reverse().Select(n => $"alice-{n}"), remaining.Select(file => file.Name));
        Assert.Equal(101, first.Concat(remaining).Select(file => file.Id).Distinct().Count());
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>("/v1/files?offset=101", Ct))!);
        Assert.Empty((await client.GetFromJsonAsync<FileEntry[]>($"/v1/files?offset={int.MaxValue}", Ct))!);
        using var bob = fixture.Client(StorageFixture.BobToken);
        Assert.Equal("bob-101", Assert.Single((await bob.GetFromJsonAsync<FileEntry[]>("/v1/files?limit=1", Ct))!).Name);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(100, 100)]
    public async Task VersionPagesRespectDefaultAndMaximumLimits(int? limit, int expected)
    {
        using var client = fixture.Client();
        var (upload, _) = await Create(client, []);
        var original = await Complete(client, upload.Id);
        await fixture.SqlAsync("""
            INSERT INTO versions (id, file_id, number, name, size, sha256, manifest)
            SELECT gen_random_uuid(), $1, n, 'version-' || n, 0, $2, '[]'::jsonb
            FROM generate_series(2, 101) AS n
            """, original.FileId, original.Sha256);
        var route = $"/v1/files/{original.FileId}/versions";
        var first = (await client.GetFromJsonAsync<FileVersion[]>(route + (limit is null ? "" : $"?limit={limit}"), Ct))!;
        Assert.Equal(Enumerable.Range(1, 101).Reverse().Take(expected), first.Select(version => version.Number));
        var remaining = (await client.GetFromJsonAsync<FileVersion[]>($"{route}?before={first[^1].Number}&limit=100", Ct))!;
        Assert.Equal(Enumerable.Range(1, 101 - expected).Reverse(), remaining.Select(version => version.Number));
        Assert.Equal(original, remaining[^1]);
        Assert.Equal(101, first.Concat(remaining).Select(version => version.Id).Distinct().Count());
        Assert.Empty((await client.GetFromJsonAsync<FileVersion[]>(route + "?before=1", Ct))!);
    }
}
