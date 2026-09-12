using System.Net.Mime;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Infrastructure.Domain;

namespace OrderFlow.Api.Middleware;

/// <summary>
/// Translates domain and persistence failures into RFC 7807 responses so controllers
/// do not have to repeat try/catch blocks.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
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
        catch (NotFoundException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status404NotFound, "Resource not found", ex.Message);
        }
        catch (InvalidStatusTransitionException ex)
        {
            _logger.LogWarning("Rejected status transition: {Message}", ex.Message);
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Invalid status transition", ex.Message);
        }
        catch (InsufficientStockException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Insufficient stock", ex.Message);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Concurrent update rejected for {Path}", context.Request.Path);
            await WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "Concurrent modification",
                "The resource was modified by another request. Reload and try again.");
        }
        catch (DomainException ex)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Invalid request", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception while processing {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "Unexpected error",
                "The request could not be processed.");
        }
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
