using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CrmLogicLens.Api.Errors;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, detail, extensions, logLevel) = exception switch
        {
            ApiInputException input => (
                input.StatusCode,
                "Invalid request",
                input.Message,
                CreateFieldExtension(input.Field),
                LogLevel.Information),
            BadHttpRequestException badRequest => (
                badRequest.StatusCode,
                "Invalid request",
                "The request body or headers are invalid.",
                null,
                LogLevel.Information),
            JsonException => (
                StatusCodes.Status400BadRequest,
                "Invalid JSON",
                "The JSON payload could not be parsed.",
                null,
                LogLevel.Information),
            _ => (
                StatusCodes.Status500InternalServerError,
                "Unexpected server error",
                "The request could not be completed. Consult the server logs with the trace identifier.",
                null,
                LogLevel.Error)
        };

        logger.Log(logLevel, exception, "Request failed with trace identifier {TraceIdentifier}", httpContext.TraceIdentifier);

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        AddExtensions(problem, extensions);

        httpContext.Response.StatusCode = statusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        });
    }

    private static Dictionary<string, object?>? CreateFieldExtension(string? field) =>
        field is null ? null : new Dictionary<string, object?> { ["field"] = field };

    private static void AddExtensions(ProblemDetails problem, Dictionary<string, object?>? extensions)
    {
        if (extensions is null)
        {
            return;
        }

        foreach (var extension in extensions)
        {
            problem.Extensions[extension.Key] = extension.Value;
        }
    }
}
