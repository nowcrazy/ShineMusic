// SqlSugar CodeFirst 建表引导：启动时幂等建表并完成空库初始化。
// - 仅在空库（全新部署）时执行：存量库结构以实际库为准（沿用历史表名与列类型），
//   SqlSugar CodeFirst 的存量表同步会把 text 收窄为 varchar(255)、误加 NOT NULL，
//   对已有数据有破坏风险，绝不自动执行；
// - 通过反射扫描 ShineMusic.Model 程序集中所有带 [SugarTable] 的实体类型，
//   新实体只要加了特性就自动纳入，无需维护手工清单；
// - XML 注释→列备注特性已在 SqlSugarFactory 经 IsNoReadXmlDescription 关闭
//   （其 PG 备注 SQL 会把列名转小写，与带引号 PascalCase 列名冲突导致崩溃）。

using System.Reflection;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace ShineMusic.Repository.Data;

/// <summary>SqlSugar CodeFirst 初始化器：空库启动时创建全部实体表结构。</summary>
public static class SqlSugarSchemaBootstrap
{
    /// <summary>库为空（核心表 AspNetUsers 不存在）时对所有 [SugarTable] 实体执行 CodeFirst 建表；否则跳过。</summary>
    public static void Initialize(ISqlSugarClient db, ILogger logger)
    {
        if (db.DbMaintenance.IsAnyTable("AspNetUsers", false))
        {
            logger.LogInformation("SqlSugar CodeFirst skipped: database schema already exists.");
            return;
        }
        var entityTypes = typeof(ShineMusic.Model.Entities.ApplicationUser).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           type.GetCustomAttribute<SugarTable>() is not null)
            .ToArray();
        logger.LogInformation("SqlSugar CodeFirst initializing {Count} entity tables (fresh database).", entityTypes.Length);
        db.CodeFirst.InitTables(entityTypes);
    }
}
