// 职责：Media 模块共享的纯格式化 / 判定 / 脱敏函数（Application 层）。
// 这些逻辑原先在 LibraryImportJob、ImportPublishService 等上帝类里各存一份，
// 容易出现「一处改了别名映射、另一处没改」的行为漂移；现在收敛为单一事实源。
// 约束：本类不得依赖 ISqlSugarClient、IHttpClientFactory 或文件系统，便于直接单测。

using ShineMusic.Model.Contracts;
using ShineMusic.Model.Domain;

namespace ShineMusic.Service;

/// <summary>导入 / 发布流水线共用的无状态规则。</summary>
public static class ImportFormatting
{
    /// <summary>解析 JSON 字符串数组；非法 JSON 一律按空列表处理（fail-closed）。复用领域层实现。</summary>
    public static List<string> DeserializeList(string? json) => PublishGate.DeserializeList(json);

    /// <summary>按扩展名推断封面 MIME 类型；未知扩展名按二进制流处理。</summary>
    public static string GetImageContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// 判断 ffprobe 探测到的容器名是否匹配允许的格式列表；
    /// 处理 ffprobe 容器名与扩展名的别名映射（matroska↔mka、asf↔wma、mov/mp4↔m4a/m4b）。
    /// </summary>
    public static bool MatchesAllowedContainer(string formatNames, IReadOnlyCollection<string> allowedFormats)
    {
        var aliases = formatNames.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowed = new HashSet<string>(allowedFormats, StringComparer.OrdinalIgnoreCase);
        return aliases.Any(alias => allowed.Contains(alias)) ||
            (aliases.Contains("matroska", StringComparer.OrdinalIgnoreCase) && allowed.Contains("mka")) ||
            (aliases.Contains("asf", StringComparer.OrdinalIgnoreCase) && allowed.Contains("wma")) ||
            (aliases.Any(alias => alias is "mov" or "mp4") && (allowed.Contains("m4a") || allowed.Contains("m4b")));
    }

    /// <summary>把内部错误码映射为面向用户的友好消息，避免泄露内部实现细节。</summary>
    public static string SafeErrorMessage(string code) => code switch
    {
        ErrorCodes.UnsupportedMediaFormat => "The file format is not supported.",
        ErrorCodes.UploadTooLarge => "The file exceeded the configured upload limit.",
        "media_processor_unavailable" => "The server media processor is not available.",
        "media_processing_timeout" => "Media processing exceeded the configured time limit.",
        ErrorCodes.MediaParseFailed => "The media file could not be parsed.",
        ErrorCodes.TranscodeFailed => "The media file could not be transcoded.",
        ErrorCodes.ResourceNotFound => "The quarantined file could not be found.",
        _ => "The import item could not be processed."
    };
}
