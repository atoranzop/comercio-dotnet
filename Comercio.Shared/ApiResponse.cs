namespace Comercio.Shared;

/// <summary>
/// Standard API response envelope for successful and failed operations.
/// </summary>
public sealed record ApiResponse<T>(
    bool Success,
    T? Data,
    ApiError? Error)
{
    public static ApiResponse<T> Succeeded(T data) =>
        new(true, data, null);

    public static ApiResponse<T> Failed(
        string code,
        string message,
        IReadOnlyDictionary<string, string[]>? details = null,
        string? traceId = null) =>
        new(false, default, new ApiError(code, message, details, traceId));
}

/// <summary>
/// Describes an API error using a stable code and safe client-facing message.
/// </summary>
public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Details = null,
    string? TraceId = null);
