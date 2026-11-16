namespace ShineMusic.Model.Contracts;

/// <summary>字段在某入口中的映射说明；Supported 不保证每首歌都有值。</summary>
public sealed record SourceMetadataFieldOriginDto(bool Supported, string Source, string Meaning);

/// <summary>同一字段在搜索与单曲详情中的来源可能不同。</summary>
public sealed record SourceMetadataFieldDto(
    string Field, SourceMetadataFieldOriginDto Search, SourceMetadataFieldOriginDto Detail);

/// <summary>独立能力配置与实现声明；运行时健康不改写配置。</summary>
public sealed record SourceProviderAbilityDto(bool Implemented, bool Enabled, string Status, string? Evidence = null);

/// <summary>音源能力目录；字段映射是代码声明，不是实时平台探测结果。</summary>
public sealed record SourceProviderDto(
    string Provider,
    int Priority,
    bool Enabled,
    string? DisabledReason,
    SourceProviderAbilityDto Metadata,
    SourceProviderAbilityDto Search,
    SourceProviderAbilityDto Download,
    IReadOnlyList<SourceMetadataFieldDto> MetadataFields);
