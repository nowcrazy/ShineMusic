// =============================================================================
// 文件：Application/LibraryImportDtoMapper.cs
// 职责：导入管理端「实体 → DTO」的只读映射与格式化，属于应用层（Application）。
// 由 LibraryImportController 尾部抽出：这里没有任何 HTTP 上下文、鉴权或业务流程，
// 只做 JSON 解析、评分明细还原、需求匹配文案、UTC 标注与音频 MIME 判断。
// 抽出的目的：Controller 只保留端点编排与状态机决策，映射规则可单独测试/复用。
// =============================================================================
using System.Text.Json;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using SqlSugar;

namespace ShineMusic.Api.Application;

/// <summary>导入条目的只读 DTO 映射；全部为无状态纯函数（除关联的批量需求查询）。</summary>
public static class LibraryImportDtoMapper
{
    public static LibraryImportBatchDto ToDto(
        LibraryImportBatch batch,
        IReadOnlyDictionary<Guid, SongRequestRecord> requests,
        SongRequestFulfillmentOptions options) => new(
        batch.Id,
        batch.Status,
        batch.Items.OrderBy(item => item.CreatedAtUtc)
            .Select(item => ToDto(item, item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var request) ? request : null, options))
            .ToArray(),
        AsUtc(batch.CreatedAtUtc));

    public static LibraryImportItemDto ToDto(
        LibraryImportItem item,
        SongRequestRecord? request,
        SongRequestFulfillmentOptions options) => new(
        item.Id,
        item.Status,
        new ImportSourceFileDto(item.DisplayName, item.SizeBytes, item.Container, item.Codec),
        new ImportMetadataDto(
            item.Title,
            DeserializeList(item.ArtistsJson),
            item.AlbumTitle,
            item.TrackNumber,
            item.DiscNumber,
            item.ReleaseYear,
            DeserializeList(item.GenresJson),
            item.DurationMs,
            item.Container,
            item.Codec,
            item.BitrateKbps,
            item.ArtworkStorageKey is not null,
            item.Lyrics,
            item.LyricsSource,
            item.LyricsConfidence),
        new ImportTranscodeDto(item.TranscodeStatus, item.TranscodeFormat, item.TranscodeBitrateKbps, item.ErrorCode),
        DeserializeList(item.WarningsJson).Concat(item.ErrorCode is null ? [] : [item.ErrorCode]).ToArray(),
        AsUtc(item.CreatedAtUtc),
        AsUtc(item.UpdatedAtUtc),
        item.SongRequestId,
        BuildMatch(item, request, options),
        item.AutoScore,
        // autoPublished：本记录是自动发布的（Published 且 SourceProvider != null 且 MetadataReviewed 在直通路径上被标记）
        item.Status == LibraryImportItemStatuses.Published && item.IsMetadataReviewed,
        ParseBreakdown(item.AutoScoreBreakdownJson));

    /// <summary>把 AutoScoreBreakdownJson 解析成强类型 DTO；JSON 缺失或不完整时返回 null。</summary>
    public static ImportScoreBreakdownDto? ParseBreakdown(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 兼容两种命名：早期写入的 PascalCase（SourceScoreDimension 直接序列化）与 camelCase。
            static bool TryGet(JsonElement el, string camelName, out JsonElement value)
            {
                if (el.TryGetProperty(camelName, out value)) return true;
                var pascal = char.ToUpperInvariant(camelName[0]) + camelName[1..];
                return el.TryGetProperty(pascal, out value);
            }

            if (!TryGet(root, "total", out var total)) return null;

            static ScoreSegmentDto? ReadSeg(JsonElement root, string name)
            {
                if (!TryGet(root, name, out var seg)) return null;
                return new ScoreSegmentDto(
                    TryGet(seg, "score", out var s) ? s.GetDouble() : 0,
                    TryGet(seg, "maxScore", out var m) ? m.GetDouble() : 0,
                    TryGet(seg, "reason", out var r) ? r.GetString() ?? string.Empty : string.Empty);
            }

            var quality = ReadSeg(root, "quality");
            var metadata = ReadSeg(root, "metadata");
            var lyrics = ReadSeg(root, "lyrics");
            if (quality is null || metadata is null || lyrics is null) return null;

            return new ImportScoreBreakdownDto(total.GetDouble(), quality, metadata, lyrics);
        }
        catch { return null; }
    }

    /// <summary>
    /// 读取时计算「待上架曲目」与「目标需求」的匹配结果：元数据被修正后提示立即刷新，
    /// 无需等到发布被拒绝才发现传错了歌。
    /// </summary>
    public static SongRequestMatchDto? BuildMatch(
        LibraryImportItem item,
        SongRequestRecord? request,
        SongRequestFulfillmentOptions options)
    {
        // 未关联需求、或元数据尚未解析出标题时无法比较，返回 null 让前端不展示提示。
        if (request is null || string.IsNullOrWhiteSpace(item.Title))
            return null;
        var check = options.Check("TitleArtistMatch");
        if (!check.IsEnabled)
            return null;

        var verdict = SongRequestMatchValidator.Validate(
            item.Title, DeserializeList(item.ArtistsJson), request, options.TitleArtistMatch);
        // blocking=true 表示即使管理员确认也无法强制关联（全局配置禁止覆盖）。
        var blocking = verdict.Kind == MatchVerdictKind.Mismatch && !check.AllowAdminOverride;
        var message = verdict.Kind switch
        {
            MatchVerdictKind.Match => $"与需求《{verdict.RequestDisplay}》一致，可直接发布。",
            MatchVerdictKind.FuzzyMatch => $"与需求《{verdict.RequestDisplay}》存在细微差异，请核对后再发布。",
            _ when blocking => $"与需求《{verdict.RequestDisplay}》不一致，发布会被拒绝。请修正歌曲名/艺人，或解绑该需求。",
            _ => $"与需求《{verdict.RequestDisplay}》不一致，发布时需填写原因强制关联。"
        };
        return new SongRequestMatchDto(
            verdict.Kind switch
            {
                MatchVerdictKind.Match => "match",
                MatchVerdictKind.FuzzyMatch => "fuzzy",
                _ => "mismatch"
            },
            verdict.TrackDisplay,
            verdict.RequestDisplay,
            blocking,
            message);
    }

    /// <summary>单条导入项的 DTO 构造（含关联需求匹配结果），供单条读写端点复用。</summary>
    public static async Task<LibraryImportItemDto> ToDtoAsync(
        LibraryImportItem item,
        ISqlSugarClient db,
        SongRequestFulfillmentOptions options,
        CancellationToken cancellationToken)
    {
        var requests = await LoadRequestsAsync(db, [item.SongRequestId], cancellationToken);
        var request = item.SongRequestId is Guid requestId && requests.TryGetValue(requestId, out var matched) ? matched : null;
        return ToDto(item, request, options);
    }

    /// <summary>批量加载导入项关联的需求，避免逐个查询（N+1）。</summary>
    public static async Task<Dictionary<Guid, SongRequestRecord>> LoadRequestsAsync(
        ISqlSugarClient db,
        IEnumerable<Guid?> requestIds,
        CancellationToken cancellationToken)
    {
        var ids = requestIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return [];
        var requests = await db.Queryable<SongRequestRecord>()
            .Where(request => ids.Contains(request.Id))
            .ToListAsync(cancellationToken);
        return requests.ToDictionary(request => request.Id);
    }

    public static List<string> DeserializeList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string GetAudioContentType(string? container) => container?.ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "wav" or "wave" => "audio/wav",
        "ogg" or "oga" => "audio/ogg",
        "flac" => "audio/flac",
        "m4a" or "aac" or "mp4" => "audio/mp4",
        _ => "application/octet-stream"
    };

    public static void DeleteStorageKey(QuarantineStoragePaths paths, string storageKey)
    {
        if (paths.TryResolve(storageKey, out var path)) TryDelete(path);
    }

    public static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException) { }
    }

    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
