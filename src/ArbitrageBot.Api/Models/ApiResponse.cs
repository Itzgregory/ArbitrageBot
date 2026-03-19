namespace ArbitrageBot.Api.Models;

public sealed class ApiResponse<T>
{
    public int Status { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; }
    public T? Data { get; init; }
    // Included in every response so callers can correlate errors
    // without having to inspect response headers
    public string? CorrelationId { get; init; }

    private ApiResponse(int status, bool success, string message, T? data, string? correlationId)
    {
        Status = status;
        Success = success;
        Message = message;
        Data = data;
        CorrelationId = correlationId;
    }

    public static ApiResponse<T> Ok(T data, string correlationId, string message = "Request successful")
        => new(200, true, message, data, correlationId);

    public static ApiResponse<T> Created(T data, string correlationId, string message = "Resource created")
        => new(201, true, message, data, correlationId);

    public static ApiResponse<T> Fail(int status, string message, string? correlationId = null)
        => new(status, false, message, default, correlationId);
}