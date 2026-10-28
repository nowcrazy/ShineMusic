// SqlSugar CodeFirst 建表引导：启动时幂等建表并完成空库初始化；
// 另提供 EnsureDatabase：--create-database 命令在连库前先把业务库本身建出来（已存在则跳过）。
// - 仅在空库（全新部署）时执行：存量库结构以实际库为准（沿用历史表名与列类型），
//   SqlSugar CodeFirst 的存量表同步会把 text 收窄为 varchar(255)、误加 NOT NULL，
//   对已有数据有破坏风险，绝不自动执行；
// - 通过反射扫描 ShineMusic.Model 程序集中所有带 [SugarTable] 的实体类型，
//   新实体只要加了特性就自动纳入，无需维护手工清单；
// - XML 注释→列备注特性已在 SqlSugarFactory 经 IsNoReadXmlDescription 关闭
//   （其 PG 备注 SQL 会把列名转小写，与带引号 PascalCase 列名冲突导致崩溃）。

using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlSugar;

namespace ShineMusic.Repository.Data;

/// <summary>SqlSugar CodeFirst 初始化器：空库启动时创建全部实体表结构。</summary>
public static class SqlSugarSchemaBootstrap
{
    /// <summary>
    /// 确保连接串中的业务库存在：不存在则创建，已存在则跳过。返回是否实际新建。
    /// 此时业务库可能还不存在、无法连接，因此改连 PG 维护库 postgres，查 pg_database 后按需建库；
    /// 库名与属主名走 NpgsqlCommandBuilder.QuoteIdentifier（CREATE DATABASE 不支持参数化标识符）。
    /// </summary>
    public static bool EnsureDatabase(string connectionString, ILogger logger)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var databaseName = builder.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
            throw new InvalidOperationException("Database:ConnectionString must specify a Database name.");

        builder.Database = "postgres";
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();

        using var existsCommand = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        existsCommand.Parameters.AddWithValue("name", databaseName);
        if (existsCommand.ExecuteScalar() is not null)
        {
            logger.LogInformation("Database {Database} already exists; creation skipped.", databaseName);
            return false;
        }

        var quote = new NpgsqlCommandBuilder();
        var owner = string.IsNullOrWhiteSpace(builder.Username)
            ? string.Empty
            : $" OWNER {quote.QuoteIdentifier(builder.Username)}";
        logger.LogInformation("Database {Database} does not exist; creating it.", databaseName);
        using var createCommand = new NpgsqlCommand($"CREATE DATABASE {quote.QuoteIdentifier(databaseName)}{owner}", connection);
        createCommand.ExecuteNonQuery();
        logger.LogInformation("Database {Database} created.", databaseName);
        return true;
    }

    /// <summary>库为空（核心表 AspNetUsers 不存在）时对所有 [SugarTable] 实体执行 CodeFirst 建表；否则仅补建缺失的新表与新列。</summary>
    public static void Initialize(ISqlSugarClient db, ILogger logger)
    {
        var entityTypes = typeof(ShineMusic.Model.Entities.ApplicationUser).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           type.GetCustomAttribute<SugarTable>() is not null)
            .ToArray();

        if (!db.DbMaintenance.IsAnyTable("AspNetUsers", false))
        {
            logger.LogInformation("SqlSugar CodeFirst initializing {Count} entity tables (fresh database).", entityTypes.Length);
            db.CodeFirst.InitTables(entityTypes);
            return;
        }

        logger.LogInformation("SqlSugar CodeFirst skipped: database schema already exists.");

        // UserPlaylistPendingTracks：SqlSugar CodeFirst 在 PG 下建表会把标识符强制转小写，
        // 而运行时用带引号的 PascalCase 标识符，两者不兼容。统一走手写 DDL 建表，
        // 并清掉历史残留的全小写表。无条件调用（内部幂等）。
        EnsurePendingTracksTable(db, logger);

        // 存量库：只对“尚不存在的表”执行建表（新实体的安全落地通道），绝不触碰已有表结构。
        foreach (var type in entityTypes)
        {
            var tableName = type.GetCustomAttribute<SugarTable>()!.TableName;
            if (tableName == "UserPlaylistPendingTracks") continue; // 已由上面的 Ensure 方法处理
            if (db.DbMaintenance.IsAnyTable(tableName, false)) continue;
            logger.LogInformation("SqlSugar CodeFirst creating missing table {Table} ({Entity}).", tableName, type.Name);
            db.CodeFirst.InitTables(type);
        }

        // 存量库：对既有表补“缺失的新列”（只增不改；列级 AddColumn 不影响既有列类型与数据）。
        EnsureColumn(db, logger, "AdminAuditEntries", nameof(ShineMusic.Model.Entities.AdminAuditEntry.Ip));
        EnsureColumn(db, logger, "AdminAuditEntries", nameof(ShineMusic.Model.Entities.AdminAuditEntry.ClientType));
        EnsureColumn(db, logger, "AdminAuditEntries", nameof(ShineMusic.Model.Entities.AdminAuditEntry.DeviceInfo));
        EnsureColumn(db, logger, "AspNetUsers", nameof(ShineMusic.Model.Entities.ApplicationUser.TempAdminUntilUtc), "timestamptz");
    }

    /// <summary>列不存在时直接 AddColumn 补可空列（默认 varchar(255)）；绝不走 CodeFirst 表同步（会误改既有列为 NOT NULL）。</summary>
    private static void EnsureColumn(ISqlSugarClient db, ILogger logger, string tableName, string columnName, string dataType = "varchar", int length = 255)
    {
        if (db.DbMaintenance.IsAnyColumn(tableName, columnName)) return;
        logger.LogInformation("SqlSugar schema patch: adding column {Table}.{Column}.", tableName, columnName);
        db.DbMaintenance.AddColumn(tableName, new DbColumnInfo
        {
            DbColumnName = columnName,
            DataType = dataType,
            // 长度仅对字符类型有意义；timestamptz 等类型传 0 避免生成 timestamptz(255) 之类的非法 DDL
            Length = dataType == "varchar" ? length : 0,
            IsNullable = true,
        });
    }

    /// <summary>
    /// 手动建 UserPlaylistPendingTracks（PascalCase 带引号），规避 SqlSugar CodeFirst 在 PG 下
    /// 强制小写标识符的问题。先清掉历史残留的全小写表，再按实体定义精确建表。
    /// </summary>
    private static void EnsurePendingTracksTable(ISqlSugarClient db, ILogger logger)
    {
        // 清掉 CodeFirst 误建的全小写表（无数据，安全删除）。
        db.Ado.ExecuteCommand("DROP TABLE IF EXISTS userplaylistpendingtracks");

        if (db.DbMaintenance.IsAnyTable("UserPlaylistPendingTracks", false)) return;

        logger.LogInformation("SqlSugar schema patch: creating table UserPlaylistPendingTracks.");
        const string ddl = """
            CREATE TABLE "UserPlaylistPendingTracks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "PlaylistId" uuid NOT NULL,
                "Title" character varying NOT NULL,
                "ArtistsJson" text NOT NULL,
                "AlbumTitle" character varying NULL,
                "DurationMs" integer NULL,
                "CoverUrl" character varying NULL,
                "SourcePlatform" character varying NOT NULL,
                "SourceTrackId" character varying NULL,
                "SourceUrl" character varying NULL,
                "SortOrder" integer NOT NULL,
                "Status" character varying NOT NULL,
                "DownloadTaskId" uuid NULL,
                "CreatedAtUtc" timestamp with time zone NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL
            );
            """;
        db.Ado.ExecuteCommand(ddl);
    }
}
