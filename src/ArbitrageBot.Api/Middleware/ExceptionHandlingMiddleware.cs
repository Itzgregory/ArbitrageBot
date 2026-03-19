using System.Net;
using ArbitrageBot.Api.Models;
using ArbitrageBot.Domain.Exceptions;

namespace ArbitrageBot.Api.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ArbitrageBotException ex)
        {
            await HandleDomainExceptionAsync(context, ex);
        }
        catch (Exception ex)
        {
            await HandleUnexpectedExceptionAsync(context, ex);
        }
    }

   private async Task HandleDomainExceptionAsync(
    HttpContext context,
    ArbitrageBotException ex)
    {
        _logger.LogWarning(ex,
            "Domain exception {ErrorCode} on {Method} {Path}",
            ex.ErrorCode,
            context.Request.Method,
            context.Request.Path);

        await WriteResponseAsync(
            context,
            HttpStatusCode.UnprocessableEntity,
            ex.ErrorCode,
            ex.Message,
            context.TraceIdentifier);
    }

    private async Task HandleUnexpectedExceptionAsync(
        HttpContext context,
        Exception ex)
    {
        _logger.LogError(ex,
            "Unhandled exception on {Method} {Path}",
            context.Request.Method,
            context.Request.Path);

        await WriteResponseAsync(
            context,
            HttpStatusCode.InternalServerError,
            "INTERNAL_ERROR",
            "An unexpected error occurred",
            context.TraceIdentifier);
    }

    private static async Task WriteResponseAsync(
        HttpContext context,
        HttpStatusCode statusCode,
        string errorCode,
        string detail,
        string? correlationId = null)
    {
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var response = ApiResponse<object>.Fail((int)statusCode, detail, correlationId);

        await context.Response.WriteAsJsonAsync(response);
    }
}