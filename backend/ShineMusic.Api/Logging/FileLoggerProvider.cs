// 文件日志提供器：把日志按 category 分流到不同目录，并按“本地日期”滚动生成日志文件。
// 设计目标：
//   1) 所有业务“操作”统一写入 logs/operations/operations-YYYY-MM-DD.log（由 category 以 "Operations" 开头触发）；
//   2) 所有“数据执行”（EF Core 实际 SQL）统一写入 logs/data/data-YYYY-MM-DD.log（由 category 以 "Data" 开头触发）；
//   3) 框架/基础设施日志（Microsoft.* 等）不落文件，仅走控制台，保证两份业务日志互不污染、彼此分离。
// 其余自定义 category 也可按前缀扩展，目前仅 Operations / Data 两类进入文件。

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
    /// 框架/基础设施日志（Microsoft.*、System.*）只走控制台，避免污染业务日志；
    /// 其余业务/服务/异常/操作/数据日志全部落盘，确保任何错误都不会只存在于控制台而被遗漏。</summary>
    private static bool IsFileTarget(string category) =>
        !category.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
        !category.StartsWith("System.", StringComparison.OrdinalIgnoreCase);

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        // 非目标 category 不落文件（仍可由控制台提供程序正常输出）。
        if (!IsFileTarget(categoryName))
            return;

        var date = DateTime.Now; // 用于文件名滚动的本地日期
        var key = $"{categoryName}|{date:yyyy-MM-dd}";
        var writer = _writers.GetOrAdd(key, _ => new FileWriter(ResolvePath(categoryName, date)));
        writer.Append(level, message, exception);
    }

    private string ResolvePath(string categoryName, DateTime date)
    {
        var (folder, prefix) = CategoryRouting(categoryName);
        Directory.CreateDirectory(folder);
        MaybePrune(folder);
        return Path.Combine(folder, $"{prefix}-{date:yyyy-MM-dd}.log");
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

    /// <summary>按 category 前缀决定目标目录与文件名前缀，实现“操作/数据”分离。</summary>
    private (string folder, string prefix) CategoryRouting(string category)
    {
        if (category.StartsWith("Operations", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "operations"), "operations");
        if (category.StartsWith("Data", StringComparison.OrdinalIgnoreCase))
            return (Path.Combine(_logsRoot, "data"), "data");
        // 业务/服务/异常等自定义日志统一进 app 目录，与“操作”“数据”分离，互不污染。
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

    public void Append(LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            _writer.Write(ts);
            _writer.Write(" [");
            _writer.Write(level.ToString().ToUpperInvariant());
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
