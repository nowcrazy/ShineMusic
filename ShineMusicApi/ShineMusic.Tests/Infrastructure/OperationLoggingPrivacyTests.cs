using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ShineMusic.Common.Logging;
using ShineMusic.Common.Middleware;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

public sealed class OperationLoggingPrivacyTests
{
    [Fact]
    public async Task Request_and_operation_logs_hide_query_secrets_without_changing_the_request()
    {
        const string token = "test-media-token-private";
        const string email = "listener@example.test";
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/admin/library/import-items/preview";
        context.Request.QueryString = new QueryString(
            $"?access_token={token}&ACCESS_TOKEN=second-private-token&email={Uri.EscapeDataString(email)}&q=private-search&page=2&pageSize=20&new_secret=private-future-value");
        context.TraceIdentifier = "privacy-test-trace";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, "test-user")], "Test"));
        var originalQuery = context.Request.QueryString;
        var log = new CapturingLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(log));
        var middleware = new OperationLoggingMiddleware(next =>
        {
            Assert.Equal(originalQuery, next.Request.QueryString);
            Assert.Equal(token, next.Request.Query["access_token"][0]);
            next.Response.StatusCode = StatusCodes.Status206PartialContent;
            return Task.CompletedTask;
        }, factory);

        await middleware.InvokeAsync(context);

        Assert.Equal(originalQuery, context.Request.QueryString);
        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry =>
        {
            Assert.Equal(context.Request.Path + "?page=2&pageSize=20", entry.Properties["Path"]);
            Assert.DoesNotContain(token, entry.Message);
            Assert.DoesNotContain("second-private-token", entry.Message);
            Assert.DoesNotContain(email, entry.Message);
            Assert.DoesNotContain("private-search", entry.Message);
            Assert.DoesNotContain("private-future-value", entry.Message);
            Assert.DoesNotContain("access_token", entry.Message, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(log.Entries, entry => entry.Category == "Debug.Request" && entry.Level == LogLevel.Debug);
        var operation = Assert.Single(log.Entries, entry => entry.Category == "Operations");
        Assert.Equal(LogLevel.Information, operation.Level);
        Assert.Equal("test-user", operation.Properties["User"]);
        Assert.Equal(206, operation.Properties["Status"]);
        Assert.Equal("privacy-test-trace", operation.TraceId);
        Assert.True(Convert.ToInt64(operation.Properties["ElapsedMs"], System.Globalization.CultureInfo.InvariantCulture) >= 0);
    }

    [Theory]
    [InlineData("?page=private-token&pageSize=20", "?pageSize=20")]
    [InlineData("?page=1&page=private-token&pageSize=20", "?pageSize=20")]
    [InlineData("?page=2147483648&pageSize=-1", "")]
    [InlineData("?page=0&pageSize=0", "")]
    [InlineData("?page=%0D%0Aprivate-token&pageSize=20", "?pageSize=20")]
    [InlineData("?page=0002&pageSize=0020", "?page=2&pageSize=20")]
    [InlineData("?PAGE=2&PAGESIZE=20&%61ccess_token=private-token", "?page=2&pageSize=20")]
    [InlineData("?pageSize=20&pageSize=30", "")]
    [InlineData("?unrecognized=private-token&private-query-key=value", "")]
    [InlineData("", "")]
    public async Task Query_allowlist_also_validates_paging_values(string query, string expectedSuffix)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tracks";
        context.Request.QueryString = new QueryString(query);
        var log = new CapturingLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(log));
        var middleware = new OperationLoggingMiddleware(_ => Task.CompletedTask, factory);

        await middleware.InvokeAsync(context);

        Assert.Equal(2, log.Entries.Count);
        Assert.All(log.Entries, entry => Assert.Equal("/api/v1/tracks" + expectedSuffix, entry.Properties["Path"]));
        Assert.Equal(query, context.Request.QueryString.Value ?? string.Empty);
    }

    [Fact]
    public async Task Failed_requests_keep_error_status_and_trace_without_exposing_query_values()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tracks";
        context.Request.QueryString = new QueryString("?access_token=private-error-token&page=3");
        context.TraceIdentifier = "privacy-failure-trace";
        var log = new CapturingLogProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(log));
        var expected = new InvalidOperationException("test failure");
        var middleware = new OperationLoggingMiddleware(_ => throw expected, factory);
        var previousTraceId = CorrelationContext.CurrentTraceId;

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Same(expected, actual);
        var operation = Assert.Single(log.Entries, entry => entry.Category == "Operations");
        Assert.Equal(LogLevel.Error, operation.Level);
        Assert.Equal(500, operation.Properties["Status"]);
        Assert.Equal("privacy-failure-trace", operation.TraceId);
        Assert.DoesNotContain("private-error-token", operation.Message);
        Assert.Equal(previousTraceId, CorrelationContext.CurrentTraceId);
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message,
        IReadOnlyDictionary<string, object?> Properties, string? TraceId);

    private sealed class CapturingLogProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose() { }
    }

    private sealed class CapturingLogger(string category, List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(category, logLevel, formatter(state, exception), properties, CorrelationContext.CurrentTraceId));
        }
    }
}
