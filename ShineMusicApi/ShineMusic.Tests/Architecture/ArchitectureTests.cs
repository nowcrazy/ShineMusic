// 职责：P6 架构约束测试（ratchet 风格）。
// 目的不是一次性证明「架构完美」，而是把已经守住的边界锁死、给剩余技术债设上限，
// 后续每次变薄一个 Controller 就把上限往下调一格，防止回退。

using System.Reflection;
using ShineMusic.Api.Extensions;
using SqlSugar;
using Xunit;

namespace ShineMusic.Tests.Architecture;

public class ArchitectureTests
{
    private static readonly Assembly ApiAssembly = typeof(DualStackApiFaces).Assembly;
    private static readonly Assembly ModelAssembly = typeof(ShineMusic.Model.Domain.PublishGate).Assembly;

    // 控制器按基类判定：同一命名空间里还就近定义了一批 request/response record，不能按命名空间粗暴扫描。
    private static IEnumerable<Type> Controllers() => ApiAssembly.GetTypes()
        .Where(type => type.Namespace == "ShineMusic.Api.Controllers"
            && type.IsClass && !type.IsAbstract
            && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(type));

    private static IEnumerable<string> OffendingControllers() => Controllers()
        .Where(controller => controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(ISqlSugarClient))))
        .Select(controller => controller.Name)
        .OrderBy(name => name, StringComparer.Ordinal);

    [Fact]
    public void Controllers_that_were_thinned_stay_free_of_sqlsugar()
    {
        var offenders = OffendingControllers().ToList();
        Assert.DoesNotContain("ArtworkController", offenders);
        Assert.DoesNotContain("RecommendationStrategyAdminController", offenders);

        // 播放端点本身也已在 P1 收口：只有它不允许再直连数据库。
        var streamTrack = Controllers().Single(c => c.Name == "CatalogController")
            .GetMethod("StreamTrack", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(streamTrack);
        Assert.DoesNotContain(streamTrack!.GetParameters(), p => p.ParameterType == typeof(ISqlSugarClient));
    }

    [Fact]
    public void Controller_sqlsugar_usage_does_not_grow()
    {
        // 基线 22 个「控制器类」（21 个文件，LyricController.cs 内含两个控制器）：
        // P3 首批变薄了 ArtworkController / RecommendationStrategyAdminController。
        // 每再瘦一个 Controller 就把这个数字减一，防止回退。
        var count = OffendingControllers().Count();
        Assert.True(count <= 22,
            $"接口层不得新增 ISqlSugarClient 直连；当前 {count} 个，基线 22。");
        Assert.NotEmpty(Controllers());
    }

    [Fact]
    public void Domain_rules_have_no_infrastructure_dependency()
    {
        var forbidden = new[] { "SqlSugar", "Microsoft.AspNetCore", "StackExchange.Redis" };
        var offenders = ReferencedNamespaces("ShineMusic.Model.Domain", ModelAssembly)
            .Where(ns => forbidden.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct()
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Domain_ports_have_no_infrastructure_dependency()
    {
        var forbidden = new[] { "SqlSugar", "Microsoft.AspNetCore", "StackExchange.Redis" };
        var offenders = ReferencedNamespaces("ShineMusic.Model.Ports", ModelAssembly)
            .Where(ns => forbidden.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct()
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_controller_is_an_api_controller()
    {
        var missing = Controllers()
            .Where(controller => controller.GetCustomAttributes(inherit: true)
                .All(attribute => attribute.GetType().Name != "ApiControllerAttribute"))
            .Select(controller => controller.Name)
            .ToList();
        Assert.Empty(missing);
    }

    private static IEnumerable<string> ReferencedNamespaces(string targetNamespace, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t.Namespace == targetNamespace))
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var referenced in MemberTypes(member))
                {
                    if (referenced.Namespace is { } ns) yield return ns;
                }
            }
        }
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        _ => []
    };
}
