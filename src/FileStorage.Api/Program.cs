using Amazon;
using Amazon.S3;
using FileStorage.Api;
using FileStorage.Core;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.AddSingleton(services => StorageSettings.Load(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services => NpgsqlDataSource.Create(
    services.GetRequiredService<IConfiguration>().GetConnectionString("Metadata") ?? throw new InvalidOperationException("ConnectionStrings:Metadata is required.")));
builder.Services.AddSingleton<IAmazonS3>(services =>
{
    var settings = services.GetRequiredService<StorageSettings>();
    var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region) };
    if (settings.Endpoint is { } endpoint)
    {
        config.ServiceURL = endpoint;
        config.AuthenticationRegion = settings.Region;
        config.ForcePathStyle = true;
    }
    return new AmazonS3Client(config);
});
builder.Services.AddSingleton<MetadataStore>();
builder.Services.AddSingleton<ObjectStore>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<FileQueries>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("storage", context => RateLimitPartition.GetConcurrencyLimiter(
        (string)context.Items["Owner"]!, _ => new ConcurrencyLimiterOptions { PermitLimit = 8, QueueLimit = 0 }));
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
var app = builder.Build();
await app.Services.GetRequiredService<MetadataStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ObjectStore>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.UseMiddleware<ApiMiddleware>();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (MetadataStore database, ObjectStore objects, CancellationToken ct) =>
{
    await using var connection = await database.OpenAsync(ct);
    await using var command = MetadataStore.Command(connection, null, "SELECT 1");
    await command.ExecuteScalarAsync(ct);
    await objects.InitializeAsync(ct);
    return Results.Ok(new { status = "ok" });
});
var api = app.MapGroup("/v1").RequireRateLimiting("storage");
api.MapPost("/uploads", async (HttpContext context, UploadRequest request, UploadService uploads, CancellationToken ct) =>
{
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    var result = await uploads.CreateAsync(Owner(context), request, key.Length == 0 ? null : key, ct);
    return Results.Created($"/v1/uploads/{result.Id}", result);
});
api.MapGet("/uploads/{id:guid}", (HttpContext context, Guid id, UploadService uploads, CancellationToken ct) => uploads.StatusAsync(Owner(context), id, ct));
api.MapPut("/uploads/{id:guid}/chunks/{hash}", async (HttpContext context, Guid id, string hash, UploadService uploads, CancellationToken ct) =>
{
    await uploads.PutAsync(Owner(context), id, hash, context.Request.Body, ct);
    return Results.NoContent();
});
api.MapPost("/uploads/{id:guid}/complete", (HttpContext context, Guid id, UploadService uploads, CancellationToken ct) => uploads.CompleteAsync(Owner(context), id, ct));
api.MapDelete("/uploads/{id:guid}", async (HttpContext context, Guid id, UploadService uploads, CancellationToken ct) =>
{
    await uploads.AbortAsync(Owner(context), id, ct);
    return Results.NoContent();
});
api.MapGet("/files", (HttpContext context, FileQueries files, CancellationToken ct, int offset = 0, int limit = 50) => files.ListAsync(Owner(context), offset, limit, ct));
api.MapGet("/files/{id:guid}/versions", (HttpContext context, Guid id, FileQueries files, CancellationToken ct, int before = int.MaxValue, int limit = 50) => files.VersionsAsync(Owner(context), id, before, limit, ct));
api.MapGet("/files/{id:guid}/versions/{version:guid}/content", async (HttpContext context, Guid id, Guid version, FileQueries files) =>
    await files.DownloadAsync(context, Owner(context), id, version));
app.Run();

static string Owner(HttpContext context) => (string)context.Items["Owner"]!;

public partial class Program;
