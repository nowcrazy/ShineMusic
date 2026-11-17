using System.Reflection;
using System.Text.Json;
using ShineMusic.Service.SourceDownload;
using Xunit;

namespace ShineMusic.Tests.Rules;

public sealed class MiguToneFallbackTests
{
    private static readonly Type Provider = typeof(MiguSourceProvider);
    private static object Resource(bool sq, bool hq) => Activator.CreateInstance(
        Provider.GetNestedType("MiguResourceInfo", BindingFlags.NonPublic)!, "content", sq, hq)!;
    private static object Invoke(string name, params object[] args) =>
        Provider.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    [Theory]
    [InlineData("HQ", true, true, "HQ,SQ,PQ")]
    [InlineData("HQ", true, false, "SQ,PQ")]
    [InlineData("HQ", false, true, "HQ,PQ")]
    [InlineData("HQ", false, false, "PQ")]
    [InlineData("SQ", true, true, "SQ,HQ,PQ")]
    [InlineData("SQ", false, true, "HQ,PQ")]
    [InlineData("PQ", true, true, "PQ")]
    [InlineData("hq", true, true, "HQ,SQ,PQ")]
    [InlineData("unknown", true, true, "HQ,SQ,PQ")]
    public void High_quality_candidates_precede_PQ(string preference, bool sq, bool hq, string expected)
    {
        var resource = Resource(sq, hq);
        var ladder = (List<string>)Invoke("BuildToneLadder", resource, preference);
        Assert.Equal(expected.Split(','), ladder);
        Assert.Equal(ladder[0], Invoke("SelectTone", resource, preference));
    }

    [Theory]
    [InlineData("newRateFormats")]
    [InlineData("rateFormats")]
    public void SQ_only_resource_is_not_misreported_as_128k_when_HQ_is_preferred(string property)
    {
        using var json = JsonDocument.Parse($"{{\"{property}\":[{{\"formatType\":\"SQ\"}},{{\"formatType\":\"PQ\"}}]}}");
        Assert.Equal(true, Invoke("HasQuality", json.RootElement, "SQ"));
        Assert.Equal(false, Invoke("HasQuality", json.RootElement, "HQ"));
        Assert.Equal(SourceQualities.Lossless, Invoke("InferQuality", json.RootElement, "HQ"));
        Assert.Equal(SourceQualities.Standard, Invoke("InferQuality", json.RootElement, "PQ"));
    }
}
