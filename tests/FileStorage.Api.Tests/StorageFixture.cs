using Amazon.Runtime;
using Amazon.S3;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net.Http.Headers;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace FileStorage.Api.Tests;

public sealed class StorageFixture : IAsyncLifetime
{
    public const string AliceToken = "alice-integration-key-0000000000000000";
    public const string BobToken = "bob-integration-key-000000000000000000";
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    public string Bucket { get; } = "test-" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    public AmazonS3Client S3 { get; private set; } = null!;
    public WebApplicationFactory<Program> App { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TEST_DATABASE") ?? "Host=localhost;Port=5432;Database=storage;Username=storage;Password=storage";
        ConnectionString = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema }.ConnectionString;
        await using (var db = new NpgsqlConnection(connection))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", db);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        S3 = new AmazonS3Client(new BasicAWSCredentials("minioadmin", "minioadmin"), new AmazonS3Config
        {
            ServiceURL = Environment.GetEnvironmentVariable("TEST_S3") ?? "http://localhost:9000",
            ForcePathStyle = true, AuthenticationRegion = "us-east-1"
        });
        App = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Metadata"] = ConnectionString,
                ["Storage:Bucket"] = Bucket,
                ["Storage:CreateBucket"] = "true",
                ["Storage:ApiKeys:alice"] = AliceToken,
                ["Storage:ApiKeys:bob"] = BobToken
            }));
            builder.ConfigureServices(services => services.AddSingleton<IAmazonS3>(S3));
        });
        using var client = Client();
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public HttpClient Client(string? token = AliceToken)
    {
        var client = App.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task SqlAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = MetadataStore.Command(connection, null, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var ct = CancellationToken.None;
        var listing = await S3.ListObjectsV2Async(new() { BucketName = Bucket }, ct);
        foreach (var item in listing.S3Objects ?? []) await S3.DeleteObjectAsync(Bucket, item.Key, ct);
        await S3.DeleteBucketAsync(Bucket, ct);
        await App.DisposeAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
        await command.ExecuteNonQueryAsync(ct);
        S3.Dispose();
    }
}
