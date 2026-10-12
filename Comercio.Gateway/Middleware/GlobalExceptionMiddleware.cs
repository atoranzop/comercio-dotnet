using Comercio.Shared;

namespace Comercio.Gateway.Middleware;

public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            var statusCode = exception is BadHttpRequestException badRequest
                ? badRequest.StatusCode
                : StatusCodes.Status500InternalServerError;
            var isClientError = statusCode < StatusCodes.Status500InternalServerError;

            if (isClientError)
            {
                logger.LogWarning(
                    exception,
                    "Rejected {RequestMethod} {RequestPath} with status {StatusCode}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    statusCode,
                    context.TraceIdentifier);
            }
            else
            {
                logger.LogError(
                    exception,
                    "Unhandled exception processing {RequestMethod} {RequestPath}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier);
            }

            context.Response.Clear();
            context.Response.StatusCode = statusCode;

            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Failed(
                    isClientError ? "invalid_request" : "internal_server_error",
                    isClientError
                        ? "The request could not be processed."
                        : "The request could not be completed.",
                    traceId: context.TraceIdentifier),
                context.RequestAborted);
        }
    }
}
