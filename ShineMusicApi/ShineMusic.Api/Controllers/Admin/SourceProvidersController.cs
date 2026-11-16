using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShineMusic.Model.Contracts;
using ShineMusic.Service.SourceProviders.Abstractions;
using ShineMusic.Service.SourceProviders.Engine;

namespace ShineMusic.Api.Controllers;

/// <summary>按元数据、搜索、下载分类的音源能力查询。</summary>
[ApiController]
[Route("api/v1/admin/source-providers")]
[Authorize(Roles = "admin")]
[Tags("Admin Source Providers")]
public sealed class SourceProvidersController : ControllerBase
{
    /// <summary>列出全部注册音源、独立能力开关与每个元数据字段的来源。</summary>
    [HttpGet]
    public IReadOnlyList<SourceProviderDto> List(ProviderRegistry registry) =>
        registry.List().Select(descriptor => new SourceProviderDto(
            descriptor.ProviderName, descriptor.Priority, descriptor.ConfigEnabled, descriptor.DisabledReason,
            Ability(descriptor, ProviderCapability.Metadata, descriptor.MetadataEnabled),
            Ability(descriptor, ProviderCapability.Search, descriptor.SearchEnabled),
            Ability(descriptor, ProviderCapability.Resolve, descriptor.DownloadEnabled),
            ProviderMetadataCatalog.GetFields(descriptor.ProviderName).Select(field => new SourceMetadataFieldDto(
                field.Field.ToString(), Origin(field.Search), Origin(field.Detail))).ToArray())).ToArray();

    /// <summary>按平台歌曲 ID 获取单曲元数据，不要求启用该平台的搜索或下载。</summary>
    [HttpGet("{provider}/tracks/{trackId}")]
    public async Task<IResult> GetMetadata(
        string provider, string trackId, ProviderRegistry registry,
        ProviderAccessService access, CancellationToken cancellationToken)
    {
        if (!registry.IsConfigured(provider, ProviderCapability.Metadata))
            return ApiResults.Error(HttpContext, StatusCodes.Status409Conflict, ErrorCodes.SourceUnavailable, "该音源未启用元数据能力。");
        var track = await access.GetMetadataAsync(provider, trackId, cancellationToken);
        if (track is null)
            return ApiResults.NotFound(HttpContext);
        return Results.Ok(new SourceSearchItemDto(track.Provider, track.ProviderTrackId, track.Title, track.Artists,
            track.AlbumTitle, track.DurationMs, track.Genres, track.CoverUrl, track.SourcePageUrl,
            track.License, track.Quality, track.IsLosslessAvailable, track.ReleaseYear));
    }

    private static SourceProviderAbilityDto Ability(ProviderDescriptor descriptor, ProviderCapability capability, bool enabled) =>
        new(descriptor.Capabilities.Any(item => item.Capability == capability),
            descriptor.ConfigEnabled && enabled && descriptor.Declares(capability), descriptor.StateOf(capability).ToString(),
            descriptor.Capabilities.FirstOrDefault(item => item.Capability == capability)?.Note);

    private static SourceMetadataFieldOriginDto Origin(MetadataFieldSource origin) =>
        new(origin.Supported, origin.Source, origin.Meaning);
}
