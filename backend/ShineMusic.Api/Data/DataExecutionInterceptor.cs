// 数据执行日志拦截器：挂在 EF Core 命令管道上，对每一条真正发送到 SQLite 的 SQL
// （查询 / 非查询 / 标量）记录命令类型、SQL 文本与参数摘要，统一写入 "DataExecution"
// category，由文件日志提供器路由到 logs/data/data-YYYY-MM-DD.log。
// 注意：记录本身包了 try/catch，任何日志异常都绝不影响数据库操作。

using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ShineMusic.Api.Data;

public sealed class DataExecutionInterceptor : DbCommandInterceptor
{
    private readonly ILogger _logger;

    public DataExecutionInterceptor(ILoggerFactory loggerFactory)
    {
        // category 以 "Data" 开头 → 文件日志提供器落入 data 目录。
        _logger = loggerFactory.CreateLogger("DataExecution");
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        LogCommand(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        LogCommand(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result)
    {
        LogCommand(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        LogCommand(command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result)
    {
        LogCommand(command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        LogCommand(command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void LogCommand(DbCommand command)
    {
        try
        {
            _logger.LogInformation(
                "SQL {CommandType} | {CommandText} | Params: {Params}",
                command.CommandType,
                command.CommandText,
                SummarizeParameters(command));
        }
        catch
        {
            // 日志失败绝不影响业务。
        }
    }

    private static string SummarizeParameters(DbCommand command)
    {
        if (command.Parameters.Count == 0)
            return "(none)";
        var builder = new StringBuilder();
        foreach (DbParameter parameter in command.Parameters)
        {
            if (builder.Length > 0)
                builder.Append("; ");
            var value = parameter.Value?.ToString() ?? "NULL";
            if (value.Length > 64)
                value = value.Substring(0, 64) + "...";
            builder.Append(parameter.ParameterName).Append('=').Append(value);
        }
        return builder.ToString();
    }
}
