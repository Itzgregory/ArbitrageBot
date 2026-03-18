using Serilog.Context;

namespace ArbitrageBot.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        AttachToContext(context, correlationId);

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        return context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString();
    }

    private static void AttachToContext(HttpContext context, string correlationId)
    {
        context.TraceIdentifier = correlationId;
        context.Response.Headers[CorrelationIdHeader] = correlationId;
    }
}