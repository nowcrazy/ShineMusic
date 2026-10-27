// 启动数据初始化服务：由 Program.cs 在 Web 主机启动前调用，完成三件幂等的初始化工作：
// 1) SqlSugar CodeFirst 建表（空库时全量建，存量库只补缺失表/列）；
// 2) 内置角色播种（user / admin / tempadmin）；
// 3) 编辑精选合集首次播种（无合集时按曲库现状生成）。
// 该服务注册为 Scoped（依赖 ISqlSugarClient 与 UserService，二者均为 Scoped），
// 调用方需通过 IServiceProvider.CreateAsyncScope() 解析后调用 InitializeAsync。

using Microsoft.Extensions.Logging;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Services;

/// <summary>启动数据初始化：CodeFirst 建表 + 角色播种 + 合集播种。</summary>
public class StartupDataInitializer
{
    private readonly ISqlSugarClient _sqlSugar;
    private readonly UserService _userService;
    private readonly ILogger<StartupDataInitializer> _logger;

    public StartupDataInitializer(ISqlSugarClient sqlSugar, UserService userService, ILogger<StartupDataInitializer> logger)
    {
        _sqlSugar = sqlSugar;
        _userService = userService;
        _logger = logger;
    }

    /// <summary>执行启动数据初始化（幂等）。</summary>
    public async Task InitializeAsync()
    {
        // 1) SqlSugar CodeFirst 幂等同步表结构。
        SqlSugarSchemaBootstrap.Initialize(_sqlSugar, _logger);

        // 2) 内置角色幂等播种：user 普通用户 / admin 管理员 / tempadmin 临时管理员。
        foreach (var roleName in new[] { "user", "admin", "tempadmin" })
            await _userService.EnsureRoleAsync(roleName);

        // 3) 编辑精选合集：首次启动且尚无合集时，按曲库现状生成真实合集（幂等，不覆盖已有）。
        var seededCollections = await EditorialCollectionSeeder.SeedIfEmptyAsync(_sqlSugar);
        if (seededCollections > 0)
            _logger.LogInformation("已按曲库现状生成 {Count} 个编辑精选合集。", seededCollections);
    }
}
