using OrderFlow.Infrastructure.Observability;

namespace OrderFlow.Api.Middleware;

/// <summary>
/// Accepts a caller supplied correlation id, or mints one, and makes it available to
/// the logging pipeline and to anything publishing downstream events.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[CorrelationContext.HttpHeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            correlationId = Guid.NewGuid().ToString("N");
        }

        context.Response.Headers[CorrelationContext.HttpHeaderName] = correlationId;
        context.Items[CorrelationContext.HttpHeaderName] = correlationId;

        using (CorrelationContext.BeginScope(correlationId))
        using (_logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId,
                   ["RequestPath"] = context.Request.Path.Value ?? string.Empty
               }))
        {
            await _next(context);
        }
    }
}
