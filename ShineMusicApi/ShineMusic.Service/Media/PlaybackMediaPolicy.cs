// 职责：发布时「播放主资产」的选取规则 + 播放层可直接 Range 输出的容器白名单。
//
// 背景（2026-11-12 音源巡检实测）：发布一条导入条目会同时保存两份资产——
//   library/original/  下载到的原始文件（实测可达 320k MP3 / 935~1567kbps FLAC）
//   library/playback/  统一 ffmpeg 转码的 AAC-in-M4A 192kbps
// 原实现无条件把转码产物设为 IsPrimary=true；播放端点按 IsPrimary 降序取资产，
// 于是即使下载到无损 FLAC，用户实际听到的仍是 192kbps 有损副本。
// 本策略让「原始文件确实更好、且播放层支持其容器」时由原始文件担任主资产，
// 转码产物保留为兼容副本（不支持 FLAC 的客户端仍可手动回落）。

using ShineMusic.Model.Entities;

namespace ShineMusic.Service;

/// <summary>播放资产选择策略（无状态，供发布与流端点共用）。</summary>
public static class PlaybackMediaPolicy
{
    private const int DefaultTranscodeBitrateKbps = 192;

    public static bool CanReuseOriginal(LibraryImportItem item) =>
        (item.Container?.ToLowerInvariant(), item.Codec?.ToLowerInvariant()) is
            ("mp3", "mp3") or ("m4a" or "mp4" or "aac", "aac");

    public static bool NeedsCompatibilityCopy(LibraryImportItem item) => !CanReuseOriginal(item);

    /// <summary>
    /// 播放端点可直接流式输出的容器 → Content-Type。
    /// 与 CatalogController 的音频流端点共用此表，避免「发布时判可播、播放时判 415」的口径漂移。
    /// </summary>
    private static readonly Dictionary<string, string> StreamableContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = "audio/mpeg",
        ["flac"] = "audio/flac",
        ["wav"] = "audio/wav",
        ["wave"] = "audio/wav",
        ["aac"] = "audio/aac",
        ["m4a"] = "audio/mp4",
        ["m4b"] = "audio/mp4",
        ["mp4"] = "audio/mp4",
        ["ogg"] = "audio/ogg",
        ["oga"] = "audio/ogg",
        ["wma"] = "audio/x-ms-wma",
        ["aiff"] = "audio/aiff",
        ["aif"] = "audio/aiff",
        ["ape"] = "audio/ape",
        ["wv"] = "audio/wavpack",
        ["mpc"] = "audio/musepack",
        ["mpp"] = "audio/musepack",
        ["dsf"] = "audio/dsf",
    };


    /// <summary>容器能否被播放端点直接输出；不支持时返回 null（对应 415）。</summary>
    public static string? TryGetContentType(string? container) =>
        container is not null && StreamableContainers.TryGetValue(container, out var mime) ? mime : null;

    /// <summary>
    /// 是否让「原始文件资产」担任播放主资产。
    /// 必须同时满足：原文件确实存在、容器可播、且音质不低于转码副本。
    /// </summary>
    /// <param name="item">导入条目（ffprobe 实测的 Container/Codec/BitrateKbps 已回填）。</param>
    /// <param name="originalFileAvailable">
    /// 原始下载文件是否仍存在。发布时会用转码产物兜底填充 original 资产，
    /// 此时文件内容其实是 m4a，绝不能按原始容器标记为 primary，否则播放端会返回错误 Content-Type。
    /// </param>
    public static bool ShouldPreferOriginal(LibraryImportItem item, bool originalFileAvailable)
    {
        if (!originalFileAvailable)
            return false;
        if (TryGetContentType(item.Container) is null)
            return false;
        if (IsLossless(item))
            return true;
        if (item.BitrateKbps is not > 0)
            return false;

        var transcodeBitrate = item.TranscodeBitrateKbps is > 0
            ? item.TranscodeBitrateKbps.Value
            : DefaultTranscodeBitrateKbps;
        return item.BitrateKbps.Value >= transcodeBitrate;
    }

    private static bool IsLossless(LibraryImportItem item) =>
        ShineMusic.Model.Domain.AudioAdmissionPolicy.IsLosslessCodec(item.Codec);
}
