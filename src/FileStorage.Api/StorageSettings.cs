using System.Text.RegularExpressions;

namespace FileStorage.Api;

public sealed class StorageSettings
{
    public string Bucket { get; set; } = "file-storage";
    public string Region { get; set; } = "us-east-1";
    public string? Endpoint { get; set; }
    public bool CreateBucket { get; set; }
    public Dictionary<string, string> ApiKeys { get; set; } = [];

    public static StorageSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetSection("Storage").Get<StorageSettings>() ?? new();
        if (settings.ApiKeys.Count == 0 || settings.ApiKeys.Any(p =>
            !Regex.IsMatch(p.Key, "^[a-z][a-z0-9_-]{0,63}$") || p.Value.Length < 32) ||
            settings.ApiKeys.Values.Distinct(StringComparer.Ordinal).Count() != settings.ApiKeys.Count)
            throw new InvalidOperationException("Configure distinct Storage:ApiKeys tokens of at least 32 characters per user.");
        if (string.IsNullOrWhiteSpace(settings.Bucket) || string.IsNullOrWhiteSpace(settings.Region))
            throw new InvalidOperationException("Storage bucket and region are required.");
        if (settings.Endpoint is not null && (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))) throw new InvalidOperationException("Invalid storage endpoint.");
        return settings;
    }
}
