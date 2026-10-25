// 职责：导入上传的共用暂存逻辑——解析 multipart 请求，把音频文件写入隔离区并流式计算
// SHA-256 内容哈希，同时把同批次的 .lrc/.txt 旁挂歌词按文件名主干与音频配对暂存。
// 管理员导入端点（/admin/library/imports）与用户上传端点（/me/uploads）共用本服务，
// 避免隔离写入、歌词配对、限流校验逻辑在两处漂移。

using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Model.Options;

namespace ShineMusic.Service;

/// <summary>上传超出单文件或批次大小上限。</summary>
public sealed class UploadLimitException : Exception;

/// <summary>上传批次中存在空文件。</summary>
public sealed class EmptyUploadException : Exception;

/// <summary>文件扩展名不在允许列表内（携带违规扩展名）。</summary>
public sealed class UnsupportedFormatException(string extension) : Exception(extension);

/// <summary>
/// 导入上传暂存服务：把 multipart 请求中的音频暂存到隔离区并计算内容哈希。
/// 线程安全（无共享状态），按请求作用域使用。
/// </summary>
public sealed class ImportStagingService
{
    // 单个旁挂歌词文件的大小上限（512 KB），超出视为无效歌词并忽略。
    private const long MaxLyricsFileBytes = 512 * 1024;
    // 流式读写缓冲区 80 KB，兼顾内存占用与吞吐。
    private const int BufferSize = 81920;

    private readonly QuarantineStoragePaths _paths;
    private readonly UploadOptions _options;

    public ImportStagingService(QuarantineStoragePaths paths, IOptions<UploadOptions> options)
    {
        _paths = paths;
        _options = options.Value;
    }

    /// <summary>暂存结果中的单个音频文件。</summary>
    /// <param name="ItemId">为本次暂存生成的条目 Id（同时用作隔离区文件名主干）。</param>
    /// <param name="DisplayName">净化后的原始文件名。</param>
    /// <param name="Extension">小写扩展名（不含点）。</param>
    /// <param name="StorageKey">隔离区存储键（uploads/{批次}/{条目Id}.{扩展名}）。</param>
    /// <param name="ContentHash">文件内容 SHA-256 十六进制串（大写）。</param>
    /// <param name="SizeBytes">文件字节数。</param>
    public sealed record StagedAudio(Guid ItemId, string DisplayName, string Extension, string StorageKey, string ContentHash, long SizeBytes);

    /// <summary>一次暂存的全部产物。</summary>
    public sealed class StagedResult
    {
        /// <summary>成功暂存的音频文件列表（按请求中的出现顺序）。</summary>
        public List<StagedAudio> Audio { get; } = [];
        /// <summary>未与任何音频配对的歌词文件名主干（孤儿歌词，调用方应记告警）。</summary>
        public List<string> OrphanLyrics { get; } = [];
        /// <summary>本次暂存写入的全部物理路径；暂存失败时服务内部已回滚清理，成功时调用方在后续流程失败时可复用。</summary>
        public List<string> CreatedPaths { get; } = [];
        /// <summary>隔离区中的批次目录名（uploads/{批次Id}），调用方写库时用于构造存储键。</summary>
        public string BatchDirectory { get; init; } = string.Empty;
        /// <summary>本批次 Id（与 BatchDirectory 中的目录名一致）。</summary>
        public Guid BatchId { get; init; }
        /// <summary>multipartart 表单集合（已由框架缓冲），供调用方读取 songRequestId 等非文件字段。</summary>
        public IFormCollection Form { get; set; } = null!;
    }

    /// <summary>
    /// 解析 multipart 请求并暂存全部音频（含旁挂歌词配对）。
    /// 任一文件超限/为空/扩展名非法时抛出对应异常；抛出前已写入的文件会被自动清理。
    /// </summary>
    public async Task<StagedResult> StageAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid();
        var batchDirectory = $"uploads/{batchId:N}";
        var result = new StagedResult { BatchDirectory = batchDirectory, BatchId = batchId };
        var createdPaths = new List<string>();

        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            result.Form = form;
            var files = form.Files;

            // 分流：.lrc/.txt 为旁挂歌词（按文件名主干索引），其余视为音频。
            var lyricsFiles = new Dictionary<string, IFormFile>(StringComparer.OrdinalIgnoreCase);
            var audioFiles = new List<IFormFile>();
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
                if (extension is "lrc" or "txt")
                {
                    if (file.Length is > 0 and <= MaxLyricsFileBytes)
                        lyricsFiles[GetFileStem(file.FileName)] = file;
                    continue;
                }
                audioFiles.Add(file);
            }

            if (audioFiles.Count > _options.MaxFilesPerBatch)
                throw new UploadLimitException();

            long totalBytes = 0;
            foreach (var file in audioFiles)
            {
                var displayName = SanitizeDisplayName(file.FileName);
                var extension = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(extension) || !_options.AllowedFormats.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    throw new UnsupportedFormatException(extension);

                var itemId = Guid.NewGuid();
                var storageKey = $"{batchDirectory}/{itemId:N}.{extension}";
                if (!_paths.TryResolve(storageKey, out var destination))
                    throw new InvalidOperationException("The upload destination is invalid.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // 流式写入隔离区并同步计算 SHA-256，避免大文件整体入内存。
                long fileBytes = 0;
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var input = file.OpenReadStream())
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[BufferSize];
                    int bytesRead;
                    while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        fileBytes += bytesRead;
                        totalBytes += bytesRead;
                        if (fileBytes > _options.MaxFileSizeBytes || totalBytes > _options.MaxBatchSizeBytes)
                            throw new UploadLimitException();
                        hash.AppendData(buffer, 0, bytesRead);
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                    if (fileBytes == 0)
                        throw new EmptyUploadException();
                    createdPaths.Add(destination);
                    result.Audio.Add(new StagedAudio(itemId, displayName, extension, storageKey, Convert.ToHexString(hash.GetHashAndReset()), fileBytes));
                }

                // 配对同名歌词：写入 {批次}/{条目Id}.lrc，与音频同目录同名，后台解析时旁挂层直接命中。
                if (lyricsFiles.Remove(GetFileStem(displayName), out var lyricsFile) &&
                    _paths.TryResolve($"{batchDirectory}/{itemId:N}.lrc", out var lyricsPath))
                {
                    await using var lyricsOutput = new FileStream(lyricsPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
                    await using var lyricsInput = lyricsFile.OpenReadStream();
                    await lyricsInput.CopyToAsync(lyricsOutput, cancellationToken);
                    await lyricsOutput.FlushAsync(cancellationToken);
                    createdPaths.Add(lyricsPath);
                }
            }

            // 收集未配对的孤儿歌词文件名，供调用方记录告警。
            result.OrphanLyrics.AddRange(lyricsFiles.Keys);
            result.CreatedPaths.AddRange(createdPaths);
            return result;
        }
        catch
        {
            // 暂存中途失败：回滚已写入的全部文件，避免隔离区残留垃圾。
            foreach (var path in createdPaths)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
            }
            // 批次目录为空时一并移除。
            try
            {
                if (_paths.TryResolve($"{batchDirectory}/.placeholder", out var placeholderPath))
                {
                    var directory = Path.GetDirectoryName(placeholderPath);
                    if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                        Directory.Delete(directory);
                }
            }
            catch (IOException) { }
            throw;
        }
    }

    /// <summary>删除单个暂存文件及其同主干 .lrc 旁挂歌词（引用命中后不再需要处理时使用）。</summary>
    public void DeleteStagedFile(string storageKey)
    {
        foreach (var key in new[] { storageKey, Path.ChangeExtension(storageKey, ".lrc") })
        {
            try { if (_paths.TryResolve(key, out var path) && File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }

    /// <summary>删除指定批次目录（用于入库写库失败后的回滚清理）。</summary>
    public void DeleteBatchDirectory(string batchDirectory)
    {
        try
        {
            if (_paths.TryResolve($"{batchDirectory}/.placeholder", out var placeholderPath))
            {
                var directory = Path.GetDirectoryName(placeholderPath);
                if (directory is not null && Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException) { }
    }

    /// <summary>取文件名主干（去目录、去扩展名、转小写），用于把 .lrc/.txt 与音频按名字配对。</summary>
    private static string GetFileStem(string? rawName) =>
        Path.GetFileNameWithoutExtension(SanitizeDisplayName(rawName)).Trim().ToLowerInvariant();

    /// <summary>净化上传文件名：截断为纯文件名、去除控制字符、限长 255。</summary>
    private static string SanitizeDisplayName(string? rawName)
    {
        var name = Path.GetFileName(rawName?.Replace('\0', ' ').Trim() ?? string.Empty);
        name = new string(name.Where(character => !char.IsControl(character)).ToArray());
        return name.Length <= 255 ? name : name[^255..];
    }
}
