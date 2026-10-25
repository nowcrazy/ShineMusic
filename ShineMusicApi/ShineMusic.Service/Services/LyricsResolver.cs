// LyricsResolver.cs
// 歌词自动补全服务：在导入流水线解析完元数据后，按「内嵌标签 → 旁挂 .lrc → 外部歌词源链」三层依次尝试，
// 命中即返回，并把来源与置信度一并带出，供审核界面判断是否需要人工核对。
//
// 设计要点：
//   1. 任何一层失败都只返回 null，绝不向上抛异常——歌词是可选内容，不能阻断导入与播放；
//   2. 外部源为多提供者链（实现见 Services/Lyrics/ExternalLyricsProviders.cs），按 LyricsOptions.ExternalSources
//      配置顺序逐源兜底（默认 lrclib → kugou），单源超时/异常只降级到下一源；
//   3. 所有外部候选都要过各源内部的 MinScore 相似度阈值，避免把同名不同版本（Live/Remix）的歌词错误写入。

using System.Text;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Model.Options;
using ShineMusic.Service.Lyrics;

namespace ShineMusic.Service;

/// <summary>
/// 一次歌词匹配的输入：来自已解析的音频元数据与隔离区源文件路径。
/// </summary>
/// <param name="Title">曲目标题（来自标签或文件名）。</param>
/// <param name="Artists">艺人名列表，取第一位作为主艺人参与匹配。</param>
/// <param name="AlbumTitle">专辑名，可为空。</param>
/// <param name="DurationMs">音频时长（毫秒），用于精确匹配与时长打分。</param>
/// <param name="EmbeddedLyrics">音频标签中的内嵌歌词（ID3 USLT 等），可为空。</param>
/// <param name="SourcePath">隔离区中的音频文件绝对路径，用于查找同名旁挂歌词。</param>
public sealed record LyricsQuery(
    string? Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle,
    long? DurationMs,
    string? EmbeddedLyrics,
    string? SourcePath);

/// <summary>
/// 一次歌词匹配的结果。
/// </summary>
/// <param name="Content">歌词正文（纯文本或带 [mm:ss] 时间轴的 LRC）。</param>
/// <param name="Source">来源标识，取值见 <see cref="LyricsSources"/>。</param>
/// <param name="Confidence">置信度 0~1：内嵌/旁挂为 1，外部源按相似度打分。</param>
/// <param name="ProviderId">外部源记录 ID（如 LRCLIB 的 id / 酷狗歌词 id），非外部来源为 null。</param>
public sealed record LyricsMatch(string Content, string Source, double Confidence, string? ProviderId);

/// <summary>
/// 歌词解析器：为导入条目自动补全歌词。
/// </summary>
public interface ILyricsResolver
{
    /// <summary>
    /// 依次尝试内嵌标签、旁挂歌词文件与外部歌词源链，返回首个命中结果。
    /// </summary>
    /// <param name="query">匹配输入。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的歌词；全部未命中时返回 null（调用方据此记录 lyrics_missing 告警）。</returns>
    Task<LyricsMatch?> ResolveAsync(LyricsQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// 基于「内嵌标签 → 旁挂 .lrc → 外部源链（LRCLIB → 酷狗）」的歌词解析实现。
/// </summary>
public sealed class LyricsResolver(
    IOptions<LyricsOptions> options,
    IEnumerable<IExternalLyricsProvider> providers,
    ILogger<LyricsResolver> logger) : ILyricsResolver
{
    /// <summary>注册到 IHttpClientFactory 的命名客户端名称（各外部源共用）。</summary>
    public const string HttpClientName = "lyrics";

    /// <summary>单份歌词的最大字节数（旁挂文件读取上限），超出视为无效。</summary>
    private const long MaxLyricsBytes = 512 * 1024;

    private readonly LyricsOptions _options = options.Value;

    /// <inheritdoc />
    public async Task<LyricsMatch?> ResolveAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        // 第 1 层：音频内嵌歌词，可信度最高，直接采用。
        var embedded = LyricsContent.Normalize(query.EmbeddedLyrics);
        if (embedded is not null)
            return new LyricsMatch(embedded, LyricsSources.Embedded, 1.0, null);

        // 第 2 层：与音频同名的旁挂歌词文件（隔离区中为 {itemId}.lrc / {itemId}.txt）。
        var sidecar = TryReadSidecar(query.SourcePath);
        if (sidecar is not null)
            return new LyricsMatch(sidecar, LyricsSources.Sidecar, 1.0, null);

        // 第 3 层：外部歌词源链。开关关闭或缺少标题时直接放弃，交由人工补充。
        if (!_options.AutoFetchEnabled || string.IsNullOrWhiteSpace(query.Title))
            return null;

        // 按配置顺序逐源兜底；单源失败/超时只降级，不影响后续源与导入流程。
        foreach (var provider in OrderProviders())
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 30)));
                var match = await provider.FetchAsync(query, timeout.Token);
                if (match is not null)
                    return match;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 单源超时：可预期的降级，继续下一源。
                logger.LogDebug("Lyrics provider {Source} timed out for {Title}.", provider.Source, query.Title);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Lyrics provider {Source} failed for {Title}.", provider.Source, query.Title);
            }
        }
        return null;
    }

    /// <summary>按 ExternalSources 配置顺序排列已注册提供者；未配置时按注册顺序全量启用。</summary>
    private IEnumerable<IExternalLyricsProvider> OrderProviders()
    {
        var sources = _options.ExternalSources;
        if (sources is not { Count: > 0 })
            return providers;
        return providers
            .Where(provider => sources.Contains(provider.Source, StringComparer.OrdinalIgnoreCase))
            .OrderBy(provider => sources.FindIndex(s => string.Equals(s, provider.Source, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// 查找并读取与音频同名的旁挂歌词文件（.lrc 优先，其次 .txt）。
    /// </summary>
    private static string? TryReadSidecar(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return null;
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return null;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        foreach (var extension in new[] { ".lrc", ".LRC", ".txt", ".TXT" })
        {
            var candidate = Path.Combine(directory, baseName + extension);
            if (!File.Exists(candidate))
                continue;
            try
            {
                if (new FileInfo(candidate).Length is 0 or > MaxLyricsBytes)
                    continue;
                var content = LyricsContent.Normalize(ReadTextFile(candidate));
                if (content is not null)
                    return content;
            }
            catch (IOException)
            {
                // 单个候选文件读取失败时继续尝试下一个扩展名。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// 读取文本文件并按 BOM 或内容推断编码：优先 UTF-8/UTF-16，
    /// 非 UTF-8（常见于 GBK 编码的 .lrc）时尝试 GB18030，运行时不支持则按 Latin1 兜底。
    /// </summary>
    private static string ReadTextFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            // 严格解码：遇到非法字节序列直接抛异常，进入下面的兜底分支。
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding("gb18030").GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 运行时未注册代码页提供程序时，退回 Latin1，保证不抛异常。
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }
}
