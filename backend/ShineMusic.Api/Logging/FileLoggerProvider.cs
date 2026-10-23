// 文件日志提供器：把日志按 category 分流到不同目录，并按“本地日期”滚动生成日志文件。
// 目录划分（均按日期滚动，文件名形如 <分类>-YYYY-MM-DD.log）：
//   logs/operations/  操作日志：每个 HTTP 请求的方法、路径、用户、状态码、耗时、IP；
//   logs/auth/        登录日志：登录成功/失败原因、令牌刷新、登出、注册、邀请校验；
//   logs/errors/      报错日志：所有 Error/Critical 或携带异常栈的日志（跨分类聚合，一处定位失败）；
//   logs/debug/       调试日志：按需显式打点（请求进入、关键分支），默认只由代码主动写入；
//   logs/data/        数据日志：EF Core 实际执行的 SQL 与参数摘要；
//   logs/app/         应用日志：其余业务/服务日志，以及框架（Microsoft.*/System.*）的 Warning 及以上。
// 同一 category 若命中错误聚合，会同时落“所属分类文件 + errors 文件”，方便单文件排查。

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShineMusic.Api.Logging;

/// <summary>文件日志的可选配置。</summary>
public sealed class FileLoggerOptions
{
    /// <summary>日志根目录（建议使用绝对路径，避免随工作目录漂移）。</summary>
    public string LogsRoot { get; set; } = "logs";

    /// <summary>日志文件最长保留天数，超过则自动清理（默认 30 天；设为 0 表示不清理）。</summary>
    public int MaxRetentionDays { get; set; } = 30;
}

/// <summary>空作用域占位（无外部作用域时返回），避免 BeginScope 抛空。</summary>
internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();
    public void Dispose() { }
}

/// <summary>ILoggerProvider 实现：按 (category, 日期) 缓存文件写入器，线程安全追加。</summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsRoot;
    private readonly int _retentionDays;
    private DateTime _lastPruneUtc;
    private bool _disposed;

    // key = "category|yyyy-MM-dd" -> 当日对应的文件写入器
    private readonly ConcurrentDictionary<string, FileWriter> _writers = new();

    public FileLoggerProvider(IOptionsMonitor<FileLoggerOptions> options)
    {
        var current = options.CurrentValue;
        _logsRoot = current.LogsRoot;
        _retentionDays = current.MaxRetentionDays;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>是否该把此 category 写入文件。
    /// 框架/基础设施日志（Microsoft.*、System.*）只保留 Warning 及以上，避免 Information 级噪音淹没业务日志；
    /// 其余业务/服务/异常/操作/数据/登录/调试日志全部按分类落盘。</summary>
    private static bool IsFileTarget(string category, LogLevel level) =>
        category.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
         || category.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            ? level >= LogLevel.Warning
            : true;

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        // 非目标 category 不落文件（仍可由控制台提供程序正常输出）。
        if (!IsFileTarget(categoryName, level))
            return;

        var date = DateTime.Now; // 用于文件名滚动的本地日期

        // 1) 主分类文件：操作 / 登录 / 调试 / SQL / 应用。
        var (folder, prefix) = CategoryRouting(categoryName);
        AppendTo(folder, prefix, categoryName, date, level, message, exception);

        // 2) 错误聚合文件：所有 Error/Critical 或带异常栈的日志再写一份到 logs/errors，
        //    排查故障时只看这一个文件即可覆盖全部失败，不必在各分类间来回翻。
        if (!categoryName.StartsWith("Errors", StringComparison.OrdinalIgnoreCase)
            && (level >= LogLevel.Error || exception is not null))
        {
            AppendTo(Path.Combine(_logsRoot, "errors"), "errors", categoryName, date, level, message, exception);
        }
    }

    /// <summary>把一条日志追加到指定目录下的当日文件（按完整路径缓存写入器，同一 category 可落多个文件）。</summary>
    private void AppendTo(string folder, string prefix, string categoryName, DateTime date,
        LogLevel level, string message, Exception? exception)
    {
        var path = Path.Combine(folder, $"{prefix}-{date:yyyy-MM-dd}.log");
        var writer = _writers.GetOrAdd(path, _ =>
        {
            Directory.CreateDirectory(folder);
            MaybePrune(folder);
            return new FileWriter(path);
        });
        writer.Append(categoryName, level, message, exception);
    }

    /// <summary>按保留天数清理过期日志：最多每 6 小时执行一次，且清理失败绝不影响正常日志写入。</summary>
    private void MaybePrune(string folder)
    {
        if (_retentionDays <= 0)
            return;
        var now = DateTime.UtcNow;
        if (now - _lastPruneUtc < TimeSpan.FromHours(6))
            return;
        _lastPruneUtc = now;
        try
        {
            var cutoff = now.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(folder, "*.log"))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.CreationTimeUtc < cutoff)
                        info.Delete();
                }
                catch
                {
                    // 单个文件删除失败忽略，继续处理其余文件。
                }
            }
        }
        catch
        {
            // 目录不可访问等异常一律忽略，日志清理不能干扰业务。
        }
    }

    /// <summary>按 category 前缀决定目标目录与文件名前缀，实现“操作/登录/错误/调试/数据/应用”分离。</summary>
    private (string folder, string prefix) CategoryRouting(string category)
    {
        if (category.StartsWith("Operations", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "operations"), "operations");
        if (category.StartsWith("Auth", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "auth"), "auth");
        if (category.StartsWith("Errors", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "errors"), "errors");
        if (category.StartsWith("Debug", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "debug"), "debug");
        if (category.StartsWith("Data", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "data"), "data");
        // 业务/服务/框架等其余日志统一进 app 目录，与上述专用日志分离，互不污染。
        return (Path.Combine(_logsRoot, "app"), "app");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var writer in _writers.Values)
            writer.Dispose();
        _writers.Clear();
    }
}

/// <summary>单个 (category, 日期) 对应的文件写入器，带锁保证行级原子写入。</summary>
internal sealed class FileWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public FileWriter(string path)
    {
        // Append + 共享读：外部可实时读取最新内容；靠 AutoFlush 写入 OS 缓冲即可，
        // 不再使用 WriteThrough（其会逐行强制落盘，日志量大时显著拖慢请求线程）。
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            8192, FileOptions.None);
        _writer = new StreamWriter(stream) { AutoFlush = true };
    }

    public void Append(string categoryName, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _writer.Write(ts);
            _writer.Write(" [");
            _writer.Write(level.ToString().ToUpperInvariant());
            _writer.Write("] [");
            // 分类名：errors 聚合文件里会混入多个来源，带上来源分类才能一眼分清是谁报的错。
            _writer.Write(categoryName);
            _writer.Write("] ");
            // 关联标识：把当前请求的 TraceId 附到每条日志，使“请求 → SQL → 异常 → 外部调用”串成同一条线索。
            var traceId = CorrelationContext.CurrentTraceId;
            if (!string.IsNullOrEmpty(traceId))
            {
                _writer.Write("[trace:");
                _writer.Write(traceId);
                _writer.Write("] ");
            }
            _writer.WriteLine(message);
            if (exception is not null)
            {
                _writer.Write("   Exception: ");
                _writer.WriteLine(exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer.Flush(); } catch { }
            _writer.Dispose();
        }
    }
}

/// <summary>单个 category 的 ILogger，把所有写入转发给 FileLoggerProvider。</summary>
internal sealed class FileLogger : ILogger
{
    private readonly FileLoggerProvider _provider;
    private readonly string _category;

    public FileLogger(FileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        var message = formatter(state, exception);
        _provider.Write(_category, logLevel, message, exception);
    }
}

/// <summary>ILoggingBuilder 扩展，注册文件日志提供器。</summary>
public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder,
        Action<FileLoggerOptions>? configure = null)
    {
        builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
        if (configure is not null)
            builder.Services.Configure(configure);
        return builder;
    }
}
