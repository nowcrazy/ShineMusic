// SqlSugarClient 工厂：全部数据访问的统一入口（按 DI 作用域创建，每个请求/每条消息/每轮定时任务各持一个）。
// 命名规范（2026-11 数据库命名重构）：表名与列名统一采用 PostgreSQL 生态惯例的 snake_case 小写、不加引号。
// 通过 ConfigureExternalServices 的 EntityNameService / PropertyNameService 钩子把 PascalCase 自动转为 snake_case，
// 95% 的表/列零改动即可落地；少数语义含糊或风格不一致的列（如 Ip→ClientIp、Quality→AudioQualityTier、
// AddedAt→AddedAtUtc 等）在 C# 实体侧改名，钩子会据此生成正确的 snake_case 列名。
// - PgSqlIsAutoToLower=false：保留钩子生成的 snake_case 原样（钩子已返回小写，无需二次转）；
// - PgSqlIsAutoToLowerCodeFirst=false：CodeFirst 建表 DDL 与查询侧对齐（同一套命名钩子）；
// - PgSqlIsAutoToLowerSchema=false：schema 名保持原样（public 不转小写无影响，但显式声明）；
// - IsNoReadXmlDescription=true：禁止 CodeFirst 读取程序集 XML 注释生成列备注——
//   该特性的 PG 备注 SQL 会把列名转小写（"Id"→"id"），与 snake_case 列名冲突直接崩溃；
// - PostgreSQL 下 Guid→uuid、DateTimeOffset→timestamptz 均为驱动原生映射；
// - Aop.OnLogExecuting 把每条实际执行的 SQL 写入 "DataExecution" category（logs/data），日志失败绝不影响数据库操作。

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
            },
            // 命名钩子：把带引号的 PascalCase 实体名/属性名统一映射为 PostgreSQL 惯例的 snake_case
            // （表名/列名小写、单词间下划线分隔、外键 *_id、布尔 is_*、时间戳 *_at_utc）。
            // 少数语义含糊列在实体侧改名后，由本钩子自动生成对应 snake_case 列名，无需逐列写 ColumnName。
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => { entity.DbTableName = ToSnakeCase(entity.DbTableName); },
                EntityService = (property, column) => { column.DbColumnName = ToSnakeCase(property.Name); }
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

    /// <summary>
    /// PascalCase / 驼峰命名转 snake_case（小写、单词间下划线分隔）。
    /// 处理连续大写缩写（Kbps→kbps、Ms→ms）、大小写交界（CreatedAtUtc→created_at_utc）、
    /// 以及「大写结尾的缩写后接小写」的分词（ReplacedByTokenHash→replaced_by_token_hash）。
    /// 仅作用于实体/属性名，不改变语义；缩写词（如 Url/Id/Ip）也会逐字符小写化。
    /// </summary>
    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                var previous = i > 0 ? name[i - 1] : '\0';
                var next = i + 1 < name.Length ? name[i + 1] : '\0';

                // 在分界处插入下划线：前一个是小写/数字（正常词界），或前一个也是大写但当前是缩写结尾
                // （前大写、当前大写、后非大写，例如 "ABc" 中的 B 之后插入，得到 a_b_c）。
                if (i > 0 && previous != '_')
                {
                    var isWordBoundary = !char.IsUpper(previous);
                    var isAbbreviationEnd = char.IsUpper(previous) && !char.IsUpper(next) && next != '\0';
                    if (isWordBoundary || isAbbreviationEnd)
                        builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
