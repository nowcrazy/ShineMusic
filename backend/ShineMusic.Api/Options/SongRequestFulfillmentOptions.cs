// 职责：点歌需求「管理员联动上传并满足需求」时的合规校验配置。
// 绑定配置节 "SongRequestFulfillment"，控制每一项检查的模式（强制/警告/关闭）
// 以及管理员是否可用原因强制跳过；其中 TitleArtistMatch 为防「张冠李戴」的核心检查。

namespace ShineMusic.Api.Options;

/// <summary>点歌满足流程的校验配置，对应配置节 "SongRequestFulfillment"。</summary>
public sealed class SongRequestFulfillmentOptions
{
    /// <summary>该选项在 appsettings 中对应的配置节名称。</summary>
    public const string SectionName = "SongRequestFulfillment";

    /// <summary>各项检查的配置，键为检查名（TitleArtistMatch / DuplicateInLibrary / ...）。</summary>
    public Dictionary<string, FulfillmentCheckOptions> Checks { get; set; } = new();

    /// <summary>标题/艺人匹配检查的细粒度参数。</summary>
    public TitleArtistMatchOptions TitleArtistMatch { get; set; } = new();

    /// <summary>管理员跳过任何检查时是否必须填写原因并写入审计；默认 true。</summary>
    public bool AdminBypassRequiresReason { get; set; } = true;

    /// <summary>取某项检查配置；未配置时回退为“强制且不可跳过”，保证安全默认。</summary>
    public FulfillmentCheckOptions Check(string name) =>
        Checks.TryGetValue(name, out var config) ? config : new FulfillmentCheckOptions();
}

/// <summary>单项检查的配置。</summary>
public sealed class FulfillmentCheckOptions
{
    /// <summary>检查模式，取值见 <see cref="FulfillmentCheckModes"/>；默认 Enforced。</summary>
    public string Mode { get; set; } = FulfillmentCheckModes.Enforced;

    /// <summary>校验失败时，管理员是否可携带原因强制通过。</summary>
    public bool AllowAdminOverride { get; set; }

    /// <summary>是否启用（Mode=Disabled 等价于关闭）。</summary>
    public bool IsEnabled => !string.Equals(Mode, FulfillmentCheckModes.Disabled, StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为强制模式（失败即阻断，除非允许覆盖）。</summary>
    public bool IsEnforced => string.Equals(Mode, FulfillmentCheckModes.Enforced, StringComparison.OrdinalIgnoreCase);
}

/// <summary>标题/艺人匹配检查参数。</summary>
public sealed class TitleArtistMatchOptions
{
    /// <summary>允许的最大编辑距离（Levenshtein），用于容忍错别字/繁简等微小差异；0 表示必须完全一致。</summary>
    public int MaxEditDistance { get; set; } = 1;

    /// <summary>比较前是否做归一化（去空白、转大写）。</summary>
    public bool Normalize { get; set; } = true;
}

/// <summary>检查模式常量。</summary>
public static class FulfillmentCheckModes
{
    /// <summary>强制：失败即阻断（除非 AllowAdminOverride 且管理员携带原因）。</summary>
    public const string Enforced = "Enforced";
    /// <summary>警告：允许通过，但需记录审计。</summary>
    public const string Warning = "Warning";
    /// <summary>关闭：不执行该检查。</summary>
    public const string Disabled = "Disabled";
}
