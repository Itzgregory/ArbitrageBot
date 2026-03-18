namespace ArbitrageBot.Api.Models;

public sealed class ApiResponse<T>
{
    public int Status { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; }
    public T? Data { get; init; }

    private ApiResponse(int status, bool success, string message, T? data)
    {
        Status = status;
        Success = success;
        Message = message;
        Data = data;
    }

    public static ApiResponse<T> Ok(T data, string message = "Request successful")
        => new(200, true, message, data);

    public static ApiResponse<T> Created(T data, string message = "Resource created")
        => new(201, true, message, data);

    public static ApiResponse<T> Fail(int status, string message)
        => new(status, false, message, default);
}