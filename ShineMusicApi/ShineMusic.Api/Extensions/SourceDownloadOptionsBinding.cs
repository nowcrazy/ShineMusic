using ShineMusic.Model.Options;

namespace ShineMusic.Api.Extensions;

/// <summary>Configured resolver lists replace defaults rather than being appended by the configuration binder.</summary>
public static class SourceDownloadOptionsBinding
{
    public static void ReplaceConfiguredNeteaseEndpoints(IConfiguration configuration, SourceDownloadOptions options)
    {
        var fallback = configuration.GetSection($"{SourceDownloadOptions.SectionName}:Netease:VipFallback");
        if (!fallback.GetChildren().Any(child => child.Key.Equals("Endpoints", StringComparison.OrdinalIgnoreCase))) return;

        options.Netease.VipFallback.Endpoints = fallback.GetSection("Endpoints").GetChildren().Select(section =>
        {
            var endpoint = new NeteaseVipFallbackEndpoint();
            if (section.GetChildren().Any(child => child.Key.Equals("Levels", StringComparison.OrdinalIgnoreCase)))
                endpoint.Levels = [];
            section.Bind(endpoint);
            return endpoint;
        }).ToList();
    }
}
