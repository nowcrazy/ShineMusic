// SqlSugarClient 工厂：全部数据访问的统一入口（按 DI 作用域创建，每个请求/每条消息/每轮定时任务各持一个）。
// - 连接串由调用方（Program.cs）传入，指向 PostgreSQL 业务库；
// - PgSqlIsAutoToLower=false：查询时表名/列名保持带引号 PascalCase（"AspNetRoles"），
//   与存量库表结构一致；
// - PgSqlIsAutoToLowerCodeFirst=false：CodeFirst 建表 DDL 同样保持带引号 PascalCase，
//   与查询侧对齐（否则 CodeFirst 建出小写表，查询报 relation does not exist）；
// - PgSqlIsAutoToLowerSchema=false：schema 名保持原样（public 不转小写无影响，但显式声明）；
// - IsNoReadXmlDescription=true：禁止 CodeFirst 读取程序集 XML 注释生成列备注——
//   该特性的 PG 备注 SQL 会把列名转小写（"Id"→"id"），与带引号 PascalCase 列名冲突直接崩溃；
// - PostgreSQL 下 Guid→uuid、DateTimeOffset→timestamptz 均为驱动原生映射，
//   SQLite 时代的自定义转换器（大写 Guid / UTC 无后缀文本）已随迁移一并移除；
// - Aop.OnLogExecuting 把每条实际执行的 SQL 写入 "DataExecution" category（logs/data），
//   日志失败绝不影响数据库操作。

using System.Text;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace ShineMusic.Repository.Data;

public static class SqlSugarFactory
{
    public static SqlSugarClient Create(string connectionString, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("DataExecution");
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                PgSqlIsAutoToLower = false,
                PgSqlIsAutoToLowerCodeFirst = false,
                PgSqlIsAutoToLowerSchema = false,
                IsNoReadXmlDescription = true
            }
        },
        db =>
        {
            db.Aop.OnLogExecuting = (sql, parameters) =>
            {
                try
                {
                    // SqlSugar 的 PostgreSQL 提供程序默认打开 Npgsql 的 legacy 时间戳行为，
                    // 导致 timestamptz 读出后被转成本机 Local 时间（08:xx UTC 读成 16:xx+08），
                    // DTO 再贴 UTC 标签就整体偏移 8 小时。每次执行前关闭该开关，读取即返回 Kind=Utc。
                    // 写入侧则要求 DateTime 必须为 Utc（Npgsql 6+ 规范），全项目时间统一用 UtcNow。
                    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
                    logger.LogInformation("SQL | {CommandText} | Params: {Params}", sql, SummarizeParameters(parameters));
                }
                catch
                {
                    // 日志失败绝不影响业务。
                }
            };
        });
    }

    private static string SummarizeParameters(SugarParameter[]? parameters)
    {
        if (parameters is null || parameters.Length == 0)
            return "(none)";
        var builder = new StringBuilder();
        foreach (var parameter in parameters)
        {
            if (builder.Length > 0)
                builder.Append("; ");
            var value = parameter.Value?.ToString() ?? "NULL";
            if (value.Length > 64)
                value = string.Concat(value.AsSpan(0, 64), "...");
            builder.Append(parameter.ParameterName).Append('=').Append(value);
        }
        return builder.ToString();
    }
}
