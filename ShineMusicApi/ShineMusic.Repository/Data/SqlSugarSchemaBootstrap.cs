// SqlSugar CodeFirst 建表引导：启动时幂等建表并完成空库初始化；
// 另提供 EnsureDatabase：--create-database 命令在连库前先把业务库本身建出来（已存在则跳过）。
// - 仅在空库（全新部署）时执行：存量库结构以实际库为准（沿用历史表名与列类型），
//   SqlSugar CodeFirst 的存量表同步会把 text 收窄为 varchar(255)、误加 NOT NULL，
//   对已有数据有破坏风险，绝不自动执行；
// - 通过反射扫描 ShineMusic.Model 程序集中所有带 [SugarTable] 的实体类型，
//   新实体只要加了特性就自动纳入，无需维护手工清单；
// - XML 注释→列备注特性已在 SqlSugarFactory 经 IsNoReadXmlDescription 关闭
//   （其 PG 备注 SQL 会把列名转小写，与 snake_case 列名冲突导致崩溃）。
// - 表名/列名统一 snake_case：SqlSugarFactory 的 EntityNameService/EntityService 钩子
//   会把 PascalCase 实体名/属性名自动转为 snake_case，DB 侧标识符均为小写下划线风格。
//   本文件中所有硬编码的表名/列名也使用 snake_case 与之一致。

using System.Reflection;
using System.Text;
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

    /// <summary>库为空（核心表 users 不存在）时对所有 [SugarTable] 实体执行 CodeFirst 建表；否则仅补建缺失的新表与新列。</summary>
    public static void Initialize(ISqlSugarClient db, ILogger logger)
    {
        var entityTypes = typeof(ShineMusic.Model.Entities.ApplicationUser).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } &&
                           type.GetCustomAttribute<SugarTable>() is not null)
            .ToArray();

        if (!db.DbMaintenance.IsAnyTable("users", false))
        {
            logger.LogInformation("SqlSugar CodeFirst initializing {Count} entity tables (fresh database).", entityTypes.Length);
            db.CodeFirst.InitTables(entityTypes);
            EnsureActiveRefreshSessionIndex(db);
            EnsureMediaLeaseIndexes(db);
            EnsureMessageOutboxIndexes(db);
            return;
        }

        logger.LogInformation("SqlSugar CodeFirst skipped: database schema already exists.");

        // user_playlist_pending_tracks / playlist_download_queue_items：SqlSugar CodeFirst 在 PG 下
        // 建表会把标识符强制转小写，而早期运行时用带引号的 PascalCase 标识符，两者不兼容。
        // 统一走手写 DDL 建表（snake_case）。保留历史表，启动期间不删除任何数据。
        EnsurePendingTracksTable(db, logger);
        EnsurePlaylistDownloadQueueTable(db, logger);

        // 存量库：只对"尚不存在的表"执行建表（新实体的安全落地通道），绝不触碰已有表结构。
        // 注意：SugarTable 特性里的表名已被钩子转成 snake_case，这里取到的是原始特性值，
        // 需要手动转 snake_case 后再查 DB。
        foreach (var type in entityTypes)
        {
            var tableName = type.GetCustomAttribute<SugarTable>()!.TableName;
            var snakeTableName = ToSnakeCase(tableName);
            if (snakeTableName == "user_playlist_pending_tracks") continue; // 已由上面的 Ensure 方法处理
            if (snakeTableName == "playlist_download_queue_items") continue;
            if (db.DbMaintenance.IsAnyTable(snakeTableName, false)) continue;
            logger.LogInformation("SqlSugar CodeFirst creating missing table {Table} ({Entity}).", snakeTableName, type.Name);
            db.CodeFirst.InitTables(type);
        }

        // 存量库：对既有表补"缺失的新列"（只增不改；列级 AddColumn 不影响既有列类型与数据）。
        // 列名使用 nameof 获取属性名后转 snake_case，与 Factory 钩子的转换结果一致。
        EnsureColumn(db, logger, "admin_audit_entries", ToSnakeCase(nameof(ShineMusic.Model.Entities.AdminAuditEntry.ClientIp)));
        EnsureColumn(db, logger, "admin_audit_entries", ToSnakeCase(nameof(ShineMusic.Model.Entities.AdminAuditEntry.ClientType)));
        EnsureColumn(db, logger, "admin_audit_entries", ToSnakeCase(nameof(ShineMusic.Model.Entities.AdminAuditEntry.DeviceInfo)));
        EnsureColumn(db, logger, "users", ToSnakeCase(nameof(ShineMusic.Model.Entities.ApplicationUser.TempAdminUntilUtc)), "timestamptz");
        EnsureColumn(db, logger, "refresh_tokens", ToSnakeCase(nameof(ShineMusic.Model.Entities.RefreshToken.SessionId)), "uuid");
        EnsureActiveRefreshSessionIndex(db);
        EnsureMediaLeaseColumns(db, logger);
        EnsureMediaLeaseIndexes(db);
        EnsureMessageOutboxIndexes(db);
        EnsureColumn(db, logger, "external_download_tasks", ToSnakeCase(nameof(ShineMusic.Model.Entities.ExternalDownloadTask.ReleaseYear)), "integer");
        EnsureColumn(db, logger, "external_download_tasks", "download_checkpoint_json", "text");
        EnsureColumn(db, logger, "user_playlist_pending_tracks", "search_diagnostics_json", "text");
        EnsureColumn(db, logger, "playlist_download_queue_items", "search_diagnostics_json", "text");
        // 元数据与导入身份升级：仅增加可空列，兼容存量数据，不重写既有列。
        foreach (var table in new[] { "tracks", "library_import_items" })
        {
            EnsureColumn(db, logger, table, "release_date");
            EnsureColumn(db, logger, table, "isrc");
            EnsureColumn(db, logger, table, "album_artists_json", "text");
            EnsureColumn(db, logger, table, "original_genres_json", "text");
            EnsureColumn(db, logger, table, "metadata_audit_json", "text");
        }
        EnsureColumn(db, logger, "user_playlists", "import_snapshot_json", "text");
        EnsureColumn(db, logger, "external_download_tasks", "expected_title", "text");
        EnsureColumn(db, logger, "external_download_tasks", "expected_artists_json", "text");
        EnsureColumn(db, logger, "external_download_tasks", "expected_album_title", "text");
        EnsureColumn(db, logger, "external_download_tasks", "expected_duration_ms", "bigint");
        // 资产回滚保留期：存量库不会执行实体表同步，必须显式补列；旧资产保持 NULL。
        EnsureMediaAssetRetentionColumn(db, logger);
        EnsureLibraryWorkflowColumns(db, logger);

        // 存量库：details_json 早期由 CodeFirst 默认建为 varchar(255)，写入完整请求快照会触发
        // PG 22001（推荐策略保存 500 的根因）。varchar→text 在 PG 中二进制兼容、不重写表，幂等升级一次。
        EnsureTextColumn(db, logger, "admin_audit_entries", ToSnakeCase(nameof(ShineMusic.Model.Entities.AdminAuditEntry.DetailsJson)));
    }

    /// <summary>仅补资产保留期列；IF NOT EXISTS 支持重复启动及并发启动，不修改已有资产。</summary>
    public static void EnsureMediaAssetRetentionColumn(ISqlSugarClient db, ILogger logger)
    {
        logger.LogInformation("SqlSugar schema patch: ensuring media_assets.retain_until_utc.");
        db.Ado.ExecuteCommand("ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS retain_until_utc timestamptz NULL");
    }

    /// <summary>Additive workflow upgrade; retains all existing catalog and import records.</summary>
    public static void EnsureLibraryWorkflowColumns(ISqlSugarClient db, ILogger logger)
    {
        logger.LogInformation("SqlSugar schema patch: ensuring library workflow columns.");
        db.Ado.ExecuteCommand("ALTER TABLE tracks ADD COLUMN IF NOT EXISTS is_recycled boolean NOT NULL DEFAULT false");
        db.Ado.ExecuteCommand("ALTER TABLE tracks ADD COLUMN IF NOT EXISTS recycled_at_utc timestamptz NULL");
        db.Ado.ExecuteCommand("ALTER TABLE tracks ADD COLUMN IF NOT EXISTS recycle_expires_at_utc timestamptz NULL");
        db.Ado.ExecuteCommand("ALTER TABLE library_import_items ADD COLUMN IF NOT EXISTS participates_in_public_review boolean NOT NULL DEFAULT false");
        db.Ado.ExecuteCommand("ALTER TABLE library_import_items ADD COLUMN IF NOT EXISTS original_storage_key varchar(255) NULL");
        foreach (var table in new[] { "library_import_items", "media_assets" })
        {
            foreach (var column in new[] { "sample_rate_hz", "bits_per_sample", "channels" })
                db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS {column} integer NULL");
            db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS codec_profile varchar(255) NULL");
        }
        db.Ado.ExecuteCommand("ALTER TABLE media_assets ADD COLUMN IF NOT EXISTS is_original boolean NOT NULL DEFAULT false");
    }

    /// <summary>
    /// 把指定字符列无损升级为 text（PG 下 varchar→text 幂等且不需要重写表）。
    /// 列不存在、已是 text、或非字符类型时直接跳过，绝不触碰其他表结构。
    /// </summary>
    private static void EnsureTextColumn(ISqlSugarClient db, ILogger logger, string tableName, string columnName)
    {
        if (!db.DbMaintenance.IsAnyTable(tableName, false)) return;

        var dataType = db.Ado.GetScalar(
            $"SELECT data_type FROM information_schema.columns " +
            $"WHERE table_schema = current_schema() AND table_name = '{tableName}' AND column_name = '{columnName}'")
            ?.ToString();
        if (string.IsNullOrWhiteSpace(dataType) ||
            string.Equals(dataType, "text", StringComparison.OrdinalIgnoreCase)) return;
        if (!dataType.Contains("char", StringComparison.OrdinalIgnoreCase) &&
            !dataType.Contains("varchar", StringComparison.OrdinalIgnoreCase)) return;

        logger.LogInformation("SqlSugar schema patch: widening {Table}.{Column} from {Type} to text.",
            tableName, columnName, dataType);
        db.Ado.ExecuteCommand($"ALTER TABLE \"{tableName}\" ALTER COLUMN \"{columnName}\" TYPE text");
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

    /// <summary>Keep the per-request live-session lookup bounded by user/session and expiry.</summary>
    private static void EnsureActiveRefreshSessionIndex(ISqlSugarClient db) =>
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_refresh_tokens_active_session ON refresh_tokens (user_id, session_id, expires_at_utc) WHERE revoked_at_utc IS NULL");

    /// <summary>为存量库补齐媒体任务租约列；全部列可空以兼容已有处理中记录。</summary>
    private static void EnsureMediaLeaseColumns(ISqlSugarClient db, ILogger logger)
    {
        logger.LogInformation("SqlSugar schema patch: ensuring media task lease columns.");
        foreach (var table in new[] { "external_download_tasks", "library_import_items" })
        {
            // IF NOT EXISTS also handles two media schedulers starting at the same time.
            db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS worker_id varchar(255) NULL");
            db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS lease_until_utc timestamptz NULL");
            db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS attempt_id uuid NULL");
            db.Ado.ExecuteCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS last_heartbeat_at_utc timestamptz NULL");
        }
    }

    /// <summary>为待投递扫描、补偿去重和死信管理查询建立索引。</summary>
    private static void EnsureMessageOutboxIndexes(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_outbox_pending ON message_outbox_entries (status, available_at_utc, lease_until_utc)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_outbox_task ON message_outbox_entries (queue_name, task_id)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_outbox_repair ON message_outbox_entries (queue_name, task_id, status, sent_at_utc)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_outbox_sent ON message_outbox_entries (status, sent_at_utc)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_dead_letters_failed ON message_dead_letters (failed_at_utc DESC, id)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_message_dead_letters_queue ON message_dead_letters (queue_name, failed_at_utc DESC, id)");
    }

    private static void EnsureMediaLeaseIndexes(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_external_download_tasks_status_lease ON external_download_tasks (status, lease_until_utc)");
        db.Ado.ExecuteCommand("CREATE INDEX IF NOT EXISTS ix_library_import_items_status_lease ON library_import_items (status, lease_until_utc)");
    }

    /// <summary>
    /// 手动建 user_playlist_pending_tracks（snake_case），规避 SqlSugar CodeFirst 在 PG 下
    /// 强制小写标识符与 PascalCase 不兼容的问题。保留历史表，按实体定义创建缺失表。
    /// </summary>
    private static void EnsurePendingTracksTable(ISqlSugarClient db, ILogger logger)
    {
        if (db.DbMaintenance.IsAnyTable("user_playlist_pending_tracks", false)) return;

        logger.LogInformation("SqlSugar schema patch: creating table user_playlist_pending_tracks.");
        const string ddl = """
            CREATE TABLE "user_playlist_pending_tracks" (
                "id" uuid NOT NULL PRIMARY KEY,
                "playlist_id" uuid NOT NULL,
                "title" character varying NOT NULL,
                "artists_json" text NOT NULL,
                "album_title" character varying NULL,
                "duration_ms" integer NULL,
                "cover_url" character varying NULL,
                "source_platform" character varying NOT NULL,
                "source_track_id" character varying NULL,
                "source_url" character varying NULL,
                "sort_order" integer NOT NULL,
                "status" character varying NOT NULL,
                "download_task_id" uuid NULL,
                "created_at_utc" timestamp with time zone NOT NULL,
                "updated_at_utc" timestamp with time zone NOT NULL,
                "search_diagnostics_json" text NULL
            );
            """;
        db.Ado.ExecuteCommand(ddl);
    }

    private static void EnsurePlaylistDownloadQueueTable(ISqlSugarClient db, ILogger logger)
    {
        if (db.DbMaintenance.IsAnyTable("playlist_download_queue_items", false)) return;

        logger.LogInformation("SqlSugar schema patch: creating table playlist_download_queue_items.");
        const string ddl = """
            CREATE TABLE "playlist_download_queue_items" (
                "id" uuid NOT NULL PRIMARY KEY,
                "user_id" uuid NOT NULL,
                "title" character varying NOT NULL,
                "artists_json" text NOT NULL,
                "album_title" character varying NULL,
                "duration_ms" integer NULL,
                "source_platform" character varying NOT NULL,
                "source_track_id" character varying NULL,
                "source_url" character varying NULL,
                "status" character varying NOT NULL,
                "download_task_id" uuid NULL,
                "created_at_utc" timestamp with time zone NOT NULL,
                "updated_at_utc" timestamp with time zone NOT NULL,
                "search_diagnostics_json" text NULL
            );
            CREATE INDEX "ix_playlist_download_queue_items_status_created_at_utc"
                ON "playlist_download_queue_items" ("status", "created_at_utc");
            """;
        db.Ado.ExecuteCommand(ddl);
    }

    /// <summary>
    /// PascalCase / 驼峰命名转 snake_case（小写、单词间下划线分隔）。
    /// 与 SqlSugarFactory.ToSnakeCase 逻辑完全一致，此处复制一份用于 SchemaBootstrap 中
    /// 把 nameof(属性名) 转换为 DB 列名（两处保持同步即可）。
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
