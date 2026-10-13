using Microsoft.Extensions.Configuration;

namespace FileStorage.Api.Tests;

public sealed class StorageSettingsTests
{
    [Fact]
    public void AwsDefaultsAreLoadedWithAValidToken()
    {
        var settings = Load(new() { ["Storage:ApiKeys:a"] = new string('x', 32) });
        Assert.Equal("file-storage", settings.Bucket);
        Assert.Equal("us-east-1", settings.Region);
        Assert.Null(settings.Endpoint);
        Assert.False(settings.CreateBucket);
        Assert.Equal(new string('x', 32), settings.ApiKeys["a"]);
    }

    [Theory]
    [InlineData("http://localhost:9000")]
    [InlineData("https://objects.example.com")]
    public void CustomStorageConfigurationIsPreserved(string endpoint)
    {
        var owner = "a" + new string('b', 63);
        var settings = Load(new()
        {
            [$"Storage:ApiKeys:{owner}"] = new string('x', 32),
            ["Storage:ApiKeys:another_user-2"] = new string('y', 32),
            ["Storage:Endpoint"] = endpoint,
            ["Storage:Bucket"] = "custom-bucket",
            ["Storage:Region"] = "ca-central-1",
            ["Storage:CreateBucket"] = "true"
        });
        Assert.Equal(endpoint, settings.Endpoint);
        Assert.Equal("custom-bucket", settings.Bucket);
        Assert.Equal("ca-central-1", settings.Region);
        Assert.True(settings.CreateBucket);
        Assert.Equal(2, settings.ApiKeys.Count);
    }

    [Fact]
    public void MissingAndDuplicateTokensAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Load([]));
        Assert.Throws<InvalidOperationException>(() => Load(new()
        {
            ["Storage:ApiKeys:alice"] = new string('x', 32),
            ["Storage:ApiKeys:bob"] = new string('x', 32)
        }));
    }

    [Theory]
    [InlineData("Storage:ApiKeys:alice", "short")]
    [InlineData("Storage:ApiKeys:1alice", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("Storage:ApiKeys:alice/bob", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("Storage:Bucket", " ")]
    [InlineData("Storage:Region", "")]
    [InlineData("Storage:Endpoint", "ftp://objects.example.com")]
    [InlineData("Storage:Endpoint", "/relative/path")]
    public void InvalidConfigurationIsRejected(string key, string value)
    {
        var values = new Dictionary<string, string?> { ["Storage:ApiKeys:alice"] = new string('x', 32), [key] = value };
        Assert.Throws<InvalidOperationException>(() => Load(values));
    }

    [Fact]
    public void OwnerNamesLongerThan64CharactersAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => Load(new() { ["Storage:ApiKeys:" + new string('a', 65)] = new string('x', 32) }));
    }

    private static StorageSettings Load(Dictionary<string, string?> values) =>
        StorageSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
