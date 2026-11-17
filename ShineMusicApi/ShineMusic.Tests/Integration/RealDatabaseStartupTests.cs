// 职责：用户 2026-11-12 明确要求「所有测试必须连接库，启动 API」。
//
// L3a（StreamDeliveryReadbackTests）刻意用 Noop 启动引导 + fake 端口做到零连库，
// 那一层抓的是「发布后 /stream 回读」的跨层断言，不能替代真机验证。
// 本类反过来：**不替换 IStartupBootstrap**，让生产实现真的连 PostgreSQL
// 跑 CodeFirst / 角色播种 / 队列恢复，再用真实 HTTP 管道打业务端点。
//
// 前置：PostgreSQL 可用；支持空数据库，不要求已有业务表或管理员账号。
// 连接串可通过 Database__ConnectionString 覆盖，未指定时使用 Api 项目配置。
// 与 L3a 共用禁并行集合，避免进程级环境变量与工作目录互相污染。

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShineMusic.Api.Services;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Integration;

[Collection(L3aCollection.Name)]
public sealed class RealDatabaseStartupTests : IClassFixture<RealApiFactory>
{
    private readonly RealApiFactory _factory;

    public RealDatabaseStartupTests(RealApiFactory factory) => _factory = factory;

    [Fact]
    public void Production_startup_bootstrap_runs_instead_of_noop()
    {
        _factory.EnsureHost();

        // 结构断言：确认宿主真的走了生产引导（建表 + 队列恢复），不是 L3a 的替身。
        Assert.IsType<StartupBootstrap>(_factory.Services.GetRequiredService<IStartupBootstrap>());
    }

    [Fact]
    public async Task Real_database_has_business_tables_and_seeded_identity_rows()
    {
        _factory.EnsureHost();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

        var tableCount = await db.Ado.GetIntAsync(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema()");
        Assert.True(tableCount >= 40, $"真库中业务表不足 40 张，实际 {tableCount}");

        var roleNames = await db.Queryable<ApplicationRole>()
            .Select(role => role.NormalizedName).ToListAsync();
        foreach (var expectedRole in new[] { "USER", "ADMIN", "TEMPADMIN" })
            Assert.Contains(expectedRole, roleNames);

        // 启动只播种角色；管理员由 --create-admin 显式创建，空库允许没有用户。
    }

    [Fact]
    public async Task Real_http_pipeline_serves_health_and_catalog_from_database()
    {
        using var client = _factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var catalog = await client.GetAsync("/api/v1/tracks?page=1&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        using var document = JsonDocument.Parse(await catalog.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.TryGetProperty("total", out _),
            "曲库端点未返回分页结构，可能没走到真实数据库查询");
    }

    [Fact]
    public void Every_mapped_entity_column_exists_after_startup()
    {
        _factory.EnsureHost();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        var missing = new List<string>();
        foreach (var type in typeof(ApplicationUser).Assembly.GetTypes()
                     .Where(type => type.GetCustomAttribute<SugarTable>() is not null))
        {
            var entity = db.EntityMaintenance.GetEntityInfo(type);
            var columns = db.DbMaintenance.GetColumnInfosByTableName(entity.DbTableName, false)
                .Select(column => column.DbColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var column in entity.Columns.Where(column => !column.IsIgnore))
                if (!columns.Contains(column.DbColumnName))
                    missing.Add($"{entity.DbTableName}.{column.DbColumnName}");
        }
        Assert.True(missing.Count == 0, "Missing mapped columns: " + string.Join(", ", missing));
    }

    [Fact]
    public void Retention_patch_is_additive_and_idempotent_for_legacy_assets()
    {
        _factory.EnsureHost();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        db.Ado.BeginTran();
        try
        {
            // A transaction-local table shadows the live table; no production rows are changed.
            db.Ado.ExecuteCommand("CREATE TEMP TABLE media_assets (id integer PRIMARY KEY, storage_path text NOT NULL) ON COMMIT DROP");
            db.Ado.ExecuteCommand("INSERT INTO media_assets VALUES (1, repeat('x', 512))");
            SqlSugarSchemaBootstrap.EnsureMediaAssetRetentionColumn(db, NullLogger.Instance);
            Assert.Equal(1, db.Ado.GetInt("SELECT count(*) FROM media_assets WHERE retain_until_utc IS NULL AND length(storage_path) = 512"));
            db.Ado.ExecuteCommand("UPDATE media_assets SET retain_until_utc = TIMESTAMPTZ '2030-01-01 00:00:00+00'");
            SqlSugarSchemaBootstrap.EnsureMediaAssetRetentionColumn(db, NullLogger.Instance);
            Assert.Equal(1, db.Ado.GetInt("SELECT count(*) FROM media_assets WHERE retain_until_utc = TIMESTAMPTZ '2030-01-01 00:00:00+00' AND length(storage_path) = 512"));
            Assert.Equal("timestamp with time zone", db.Ado.GetString("SELECT data_type FROM information_schema.columns WHERE table_schema LIKE 'pg_temp_%' AND table_name = 'media_assets' AND column_name = 'retain_until_utc'"));
        }
        finally
        {
            db.Ado.RollbackTran();
        }
    }

    [Fact]
    public async Task Admin_endpoints_reject_anonymous_callers()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/admin/library/import-items?page=1&pageSize=1");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"管理端点未做鉴权，匿名返回 {(int)response.StatusCode}");
    }

    [Fact]
    public void No_business_worker_is_started_in_test_host()
    {
        _factory.EnsureHost();

        Assert.DoesNotContain(_factory.Services.GetServices<IHostedService>(),
            service => service.GetType().Namespace?.StartsWith("ShineMusic.Tasks", StringComparison.Ordinal) == true);
    }
}

/// <summary>
/// 真机测试宿主：只覆盖必需的 JWT 签名键与 Worker 模式，
/// **不**替换 IStartupBootstrap / ITrackPlaybackStore / IAudioStorage。
/// </summary>
public sealed class RealApiFactory : WebApplicationFactory<Program>
{
    private static readonly (string Key, string Value)[] EnvironmentOverrides =
    [
        ("Auth__SigningKey", "Real-Db-Integration-Test-SigningKey-abcdefgh-1234567890"),
        ("Worker__Mode", "none"),
    ];

    private readonly Dictionary<string, string?> _previousEnvironment = [];
    private readonly string _previousWorkingDirectory = Environment.CurrentDirectory;

    public RealApiFactory()
    {
        foreach (var (key, value) in EnvironmentOverrides)
        {
            _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        Environment.CurrentDirectory = ResolveApiProjectDirectory();
    }

    public void EnsureHost() => _ = Server;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(ResolveApiProjectDirectory());
        builder.ConfigureLogging(logging => logging.ClearProviders());
        // 刻意不调用 ConfigureTestServices：生产 StartupBootstrap 会连库并恢复队列。
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.CurrentDirectory = _previousWorkingDirectory;
            foreach (var (key, previous) in _previousEnvironment)
                Environment.SetEnvironmentVariable(key, previous);
        }
        base.Dispose(disposing);
    }

    private static string ResolveApiProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ShineMusic.Api", "appsettings.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }
        throw new InvalidOperationException(
            "找不到 ShineMusic.Api/appsettings.json：真机测试需要从测试输出目录上溯到 Api 项目。");
    }
}
