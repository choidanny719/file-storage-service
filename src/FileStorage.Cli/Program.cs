using System.Net.Http.Headers;
using System.Text.Json;
using FileStorage.Cli;
using FileStorage.Core;

if (args.Length == 0 || args[0] is "--help" or "help")
{
    Console.WriteLine("upload <path> [--file-id <id>] [--state <path>]\ndownload <file-id> <version-id> <destination>\nlist [offset]\nversions <file-id> [before]\nSet FILE_STORAGE_URL and FILE_STORAGE_TOKEN.");
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var url = Environment.GetEnvironmentVariable("FILE_STORAGE_URL") ?? "http://localhost:8080";
    if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        throw new ArgumentException("FILE_STORAGE_URL must be an HTTP or HTTPS URL.");
    var token = Environment.GetEnvironmentVariable("FILE_STORAGE_TOKEN");
    if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("FILE_STORAGE_TOKEN is required.");
    using var http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(10) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var client = new StorageClient(http);
    var ct = cancellation.Token;
    object? result;
    switch (args[0])
    {
        case "upload" when args.Length >= 2:
            Guid? fileId = null;
            var state = args[1] + ".upload.json";
            var seen = new HashSet<string>();
            for (var i = 2; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !seen.Add(args[i])) throw new ArgumentException("Invalid upload options.");
                switch (args[i])
                {
                    case "--file-id": fileId = Guid.Parse(args[i + 1]); break;
                    case "--state": state = args[i + 1]; break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            result = await client.UploadAsync(args[1], fileId, state, ct);
            break;
        case "download" when args.Length == 4:
            await client.DownloadAsync(Guid.Parse(args[1]), Guid.Parse(args[2]), args[3], ct);
            result = new { saved = args[3] };
            break;
        case "list" when args.Length is 1 or 2:
            result = await client.GetAsync<FileEntry[]>($"v1/files?offset={(args.Length == 2 ? int.Parse(args[1]) : 0)}", ct);
            break;
        case "versions" when args.Length is 2 or 3:
            result = await client.GetAsync<FileVersion[]>($"v1/files/{Guid.Parse(args[1])}/versions?before={(args.Length == 3 ? int.Parse(args[2]) : int.MaxValue)}", ct);
            break;
        default: throw new ArgumentException("Invalid command. Run with --help for usage.");
    }
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Canceled. Upload progress is saved in the state file."); return 130; }
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or HttpRequestException or InvalidOperationException or JsonException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
