using System.Text.Json;
using Comercio.Gateway.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Comercio.IntegrationTests;

public sealed class GlobalExceptionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsGenericApiResponseWithoutExceptionMessage()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/orders/checkout";
        context.Response.Body = new MemoryStream();
        var logger = new CapturingLogger();
        var middleware = new GlobalExceptionMiddleware(
            _ => throw new InvalidOperationException("Sensitive database details."),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.StartsWith("application/json", context.Response.ContentType);

        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        var root = response.RootElement;

        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(
            "internal_server_error",
            root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            context.TraceIdentifier,
            root.GetProperty("error").GetProperty("traceId").GetString());
        Assert.DoesNotContain("Sensitive database details.", root.ToString());
        var logEntry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, logEntry.LogLevel);
        Assert.IsType<InvalidOperationException>(logEntry.Exception);
        Assert.Contains("POST", logEntry.Message);
        Assert.Contains("/api/orders/checkout", logEntry.Message);
        Assert.Contains(context.TraceIdentifier, logEntry.Message);
    }

    private sealed class CapturingLogger : ILogger<GlobalExceptionMiddleware>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(
                logLevel,
                formatter(state, exception),
                exception));
        }
    }

    private sealed record LogEntry(
        LogLevel LogLevel,
        string Message,
        Exception? Exception);
}
