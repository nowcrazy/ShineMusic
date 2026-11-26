using Microsoft.Extensions.Logging;
using ShineMusic.Repository.Data;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Infrastructure;

/// <summary>直接执行生产 AOP 回调验证日志隐私，不打开数据库连接。</summary>
public sealed class SqlLoggingPrivacyTests
{
    [Fact]
    public void Sql_log_keeps_template_and_parameter_names_without_formatting_values()
    {
        using var factory = new CapturingLoggerFactory();
        using var db = CreateClient(factory);
        const string sql = "SELECT id FROM users WHERE email = @email AND token_hash = @token";
        const string email = "private-user@example.test";
        const string token = "test-secret-token-never-log";
        var longValue = new string('X', 1000);
        var opaque = new UnformattableValue();
        var parameters = new[]
        {
            new SugarParameter("@email", email),
            new SugarParameter("@token", token),
            new SugarParameter("@long_value", longValue),
            new SugarParameter("@opaque", opaque)
        };

        db.CurrentConnectionConfig.AopEvents.OnLogExecuting(sql, parameters);

        var message = Assert.Single(factory.Logger.Messages);
        Assert.Contains(sql, message);
        Assert.Contains("ParameterNames: @email; @token; @long_value; @opaque", message);
        Assert.DoesNotContain(email, message);
        Assert.DoesNotContain(token, message);
        Assert.DoesNotContain(longValue[..64], message);
        Assert.False(opaque.WasFormatted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sql_without_parameters_still_logs(bool useNull)
    {
        using var factory = new CapturingLoggerFactory();
        using var db = CreateClient(factory);

        db.CurrentConnectionConfig.AopEvents.OnLogExecuting("SELECT 1", useNull ? null! : []);

        var message = Assert.Single(factory.Logger.Messages);
        Assert.Contains("SELECT 1", message);
        Assert.Contains("ParameterNames: (none)", message);
    }

    [Fact]
    public void Disabled_logging_does_not_emit_a_sql_record()
    {
        using var factory = new CapturingLoggerFactory();
        factory.Logger.Enabled = false;
        using var db = CreateClient(factory);
        var opaque = new UnformattableValue();

        db.CurrentConnectionConfig.AopEvents.OnLogExecuting("SELECT @value", [new SugarParameter("@value", opaque)]);

        Assert.Empty(factory.Logger.Messages);
        Assert.False(opaque.WasFormatted);
    }

    [Fact]
    public void Logging_failure_does_not_interrupt_database_callback()
    {
        using var factory = new CapturingLoggerFactory();
        factory.Logger.ThrowOnLog = true;
        using var db = CreateClient(factory);

        var exception = Record.Exception(() => db.CurrentConnectionConfig.AopEvents.OnLogExecuting("SELECT @value", [new SugarParameter("@value", "private")]));

        Assert.Null(exception);
    }

    private static SqlSugarClient CreateClient(ILoggerFactory factory) => SqlSugarFactory.Create(
        "Host=127.0.0.1;Database=unused_logging_test;Username=test;Password=test", factory);

    private sealed class UnformattableValue
    {
        public bool WasFormatted { get; private set; }

        public override string ToString()
        {
            WasFormatted = true;
            throw new InvalidOperationException("Parameter values must never be formatted for logging.");
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();
        public ILogger CreateLogger(string categoryName) => Logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public bool Enabled { get; set; } = true;
        public bool ThrowOnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnLog)
                throw new InvalidOperationException("Injected log failure.");
            Messages.Add(formatter(state, exception));
        }
    }
}
