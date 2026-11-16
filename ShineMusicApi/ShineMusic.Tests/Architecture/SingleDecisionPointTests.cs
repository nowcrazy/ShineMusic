// 职责：防止「曲目身份匹配实现」再次被复制成分叉的影子副本。
//
// 背景（docs/音源质量稳定性策略.md §1.2 / §4.2）：§11.3 只修了 SourceDownloadJob.TitleMatches
// （子串包含 → 全等），SourceDownloadFallbackService 里另有一份 private 复制实现仍是旧逻辑；
// 15 个只断言 Job internal 方法的用例测不到它 → 146/146 全绿但换源路径《等晴天》照样被收进来。
// 普通用例测「某个实现对不对」，这里测「是不是只有这一个实现」——测结构，不是测逻辑。

using System.Reflection;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Architecture;

public sealed class SingleDecisionPointTests
{
    private const BindingFlags DeclaredMethods =
        BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance
        | BindingFlags.DeclaredOnly;

    private static readonly Assembly ServiceAssembly = typeof(TrackIdentityMatcher).Assembly;
    private static readonly Assembly ApiAssembly = typeof(ShineMusic.Api.Extensions.DualStackApiFaces).Assembly;

    private static readonly string[] GuardedNames = ["TitleMatches", "ArtistsMatch", "NormalizeTitle"];

    private static IEnumerable<Assembly> ProductionAssemblies() => [ServiceAssembly, ApiAssembly];

    private static IEnumerable<MethodInfo> Definitions(IEnumerable<Assembly> assemblies, string name)
        => assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(DeclaredMethods))
            .Where(method => method.Name == name)
            // 白名单：UserUploadController.NormalizeTitle 只做上传文件名 trim + lowercase 去重，
            // 与曲目身份判定语义不同，不参与单一决策点约束（按类名匹配，避免依赖命名空间）。
            .Where(method => !(method.Name == "NormalizeTitle"
                               && method.DeclaringType?.Name == "UserUploadController"));

    [Theory]
    [InlineData("TitleMatches")]
    [InlineData("ArtistsMatch")]
    [InlineData("NormalizeTitle")]
    public void Identity_matching_has_exactly_one_definition(string methodName)
    {
        var definitions = Definitions(ProductionAssemblies(), methodName).ToList();

        Assert.Single(definitions);
        Assert.Equal(typeof(TrackIdentityMatcher), definitions[0].DeclaringType);
    }

    [Fact]
    public void Download_paths_have_no_local_matching_implementation()
    {
        // 即使有人把复制实现改了名字，只要还挂在下载编排的两个类里，这条也会红。
        var offenders = new[] { typeof(SourceDownloadJob), typeof(SourceDownloadFallbackService) }
            .SelectMany(type => type.GetMethods(DeclaredMethods))
            .Where(method => GuardedNames.Contains(method.Name, StringComparer.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Detector_would_catch_a_copied_shadow_implementation()
    {
        // 故障注入：把含一个故意哨兵副本的测试程序集加入扫描范围。
        // 检测器必须报出 >1 个定义，否则「全仓定义 = 1」的断言只是摆设。
        var withShadow = ProductionAssemblies().Append(typeof(ShadowCopy).Assembly);
        var definitions = Definitions(withShadow, "TitleMatches").ToList();

        Assert.True(definitions.Count > 1,
            $"检测器未能发现影子副本：只找到 {definitions.Count} 个 TitleMatches 定义。");
        Assert.Contains(definitions, method => method.DeclaringType == typeof(ShadowCopy));
    }

    /// <summary>仅用于验证检测器有效性的哨兵；不参与生产代码。</summary>
    private sealed class ShadowCopy
    {
        // 故意复制一个同名实现：若有人在生产程序集里这么做，上面的 Theory 会立刻变红。
        internal static bool TitleMatches(string left, string? right) => left == right;
    }
}
