using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Npgsql;

namespace FileStorage.Api;

public sealed class ApiMiddleware(RequestDelegate next, StorageSettings settings, ILogger<ApiMiddleware> logger)
{
    private readonly (string Owner, byte[] Digest)[] keys = settings.ApiKeys.Select(k => (k.Key, SHA256.HashData(Encoding.UTF8.GetBytes(k.Value)))).ToArray();

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            if (context.Request.Path.StartsWithSegments("/v1"))
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : "";
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
                string? owner = null;
                foreach (var key in keys)
                    if (CryptographicOperations.FixedTimeEquals(key.Digest, digest)) owner = key.Owner;
                if (owner is null)
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    throw new ApiFault(401, "unauthorized", "A valid bearer token is required.");
                }
                context.Items["Owner"] = owner;
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (Exception ex)
        {
            var (status, code, message) = ex switch
            {
                ApiFault fault => (fault.Status, fault.Code, fault.Message),
                BadHttpRequestException bad => (bad.StatusCode, "invalid_request", "Invalid request body or parameters."),
                AmazonS3Exception or NpgsqlException or HttpRequestException or TimeoutException => (503, "storage_unavailable", "Storage is unavailable; retry the request."),
                _ => (500, "internal_error", "Request failed.")
            };
            if (status >= 500) logger.LogError(ex, "Request failed with {Code}", code);
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.Clear();
            context.Response.StatusCode = status;
            if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new { error = code, message }, context.RequestAborted);
        }
    }
}
