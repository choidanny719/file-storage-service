using System.Text.Json;
using Amazon.S3;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FileStorage.Api.Tests;

public sealed class ApiMiddlewareTests
{
    [Theory]
    [InlineData("validation", 400, "invalid_request", "Invalid manifest.")]
    [InlineData("body", 413, "invalid_request", "Invalid request body or parameters.")]
    [InlineData("s3", 503, "storage_unavailable", "Storage is unavailable; retry the request.")]
    [InlineData("database", 503, "storage_unavailable", "Storage is unavailable; retry the request.")]
    [InlineData("http", 503, "storage_unavailable", "Storage is unavailable; retry the request.")]
    [InlineData("timeout", 503, "storage_unavailable", "Storage is unavailable; retry the request.")]
    [InlineData("unexpected", 500, "internal_error", "Request failed.")]
    public async Task ErrorsKeepTheirStatusAndDoNotExposeInternalDetails(string failure, int status, string code, string message)
    {
        const string details = "private storage connection details";
        Exception error = failure switch
        {
            "validation" => ApiFault.Invalid("Invalid manifest."),
            "body" => new BadHttpRequestException(details, 413),
            "s3" => new AmazonS3Exception(details),
            "database" => new NpgsqlException(details),
            "http" => new HttpRequestException(details),
            "timeout" => new TimeoutException(details),
            _ => new InvalidOperationException(details)
        };
        var context = new DefaultHttpContext();
        context.Request.Path = "/health/ready";
        context.RequestAborted = TestContext.Current.CancellationToken;
        using var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new ApiMiddleware(request =>
        {
            request.Response.Headers["X-Internal-Debug"] = details;
            return Task.FromException(error);
        }, new StorageSettings(), NullLogger<ApiMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("X-Internal-Debug"));
        body.Position = 0;
        using var response = await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(code, response.RootElement.GetProperty("error").GetString());
        Assert.Equal(message, response.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, response.RootElement.EnumerateObject().Count());
    }
}
