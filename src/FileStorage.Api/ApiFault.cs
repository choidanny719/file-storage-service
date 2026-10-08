namespace FileStorage.Api;

public sealed class ApiFault(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static ApiFault Missing() => new(404, "not_found", "Resource not found.");
    public static ApiFault Invalid(string message) => new(400, "invalid_request", message);
    public static ApiFault Conflict(string message) => new(409, "conflict", message);
}
