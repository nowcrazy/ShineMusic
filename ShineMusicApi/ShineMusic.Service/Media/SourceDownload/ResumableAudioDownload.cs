using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ShineMusic.Service.SourceDownload;

/// <summary>
/// 当前 attempt 内的有界下载恢复；仅在强 ETag 与总长均已知时追加字节。
/// RFC 9110 §13.1.5 禁止将弱 ETag 用于 If-Range；缺少验证器时从头重下。
/// </summary>
internal static class ResumableAudioDownload
{
    private const int MaximumRecoveries = 2;
    private const int BufferSize = 81920;

    internal static async Task<(SourceDownloadInfo Candidate, string ContentHash, long SizeBytes)> DownloadAsync(
        HttpClient client, SourceDownloadInfo candidate,
        Func<SourceDownloadInfo, CancellationToken, Task<SourceDownloadInfo>>? refresh,
        string audioPath, long maxBytes, CancellationToken cancellationToken, DownloadCheckpointStore? checkpoint = null)
    {
        var current = candidate;
        var refreshed = false;
        var recoveries = 0;
        long offset = 0;
        long? totalLength = null;
        string? strongTag = null;

        void ResetPartial()
        {
            offset = 0;
            totalLength = null;
            strongTag = null;
        }

        async Task RecoverAsync(HttpStatusCode? statusCode = null)
        {
            if (recoveries++ >= MaximumRecoveries)
                throw new HttpRequestException("The audio source recovery budget was exhausted.", null, statusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(100 * recoveries), cancellationToken);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (refresh is not null && current.ExpiresAtUtc is { } expiry
                && expiry <= DateTimeOffset.UtcNow.AddSeconds(5))
            {
                refreshed = true;
                current = await refresh(current, cancellationToken);
                if (checkpoint is not null) await checkpoint.ClearAsync(cancellationToken);
            }
            else if (checkpoint is not null && await checkpoint.RestoreAsync(current, audioPath, maxBytes, cancellationToken) is { } restored)
            {
                offset = restored.WrittenBytes;
                totalLength = restored.TotalLength;
                strongTag = restored.StrongETag;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A new signed URL need not address the same byte representation. Refresh always resets this state.
                var resumeAt = offset > 0 && totalLength > offset && strongTag is not null ? offset : 0;
                if (resumeAt == 0) ResetPartial();
                try
                {
                    using var response = await OpenResponseAsync(client, current, resumeAt, strongTag, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        throw new HttpRequestException("The audio source returned an encoded byte representation.", null, response.StatusCode);

                    long? responseLength;
                    if (response.StatusCode == HttpStatusCode.PartialContent && resumeAt > 0)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (range is null || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
                            || !range.HasRange || !range.HasLength || range.From != resumeAt
                            || range.Length != totalLength || range.To != totalLength - 1)
                            throw new HttpRequestException("The audio source returned an invalid resume range.", null, response.StatusCode);
                        if (StrongTag(response) != strongTag)
                        {
                            // Never append a representation with an absent, weak, or changed validator.
                            if (checkpoint is not null) await checkpoint.ClearAsync(cancellationToken);
                            await RecoverAsync(response.StatusCode);
                            ResetPartial();
                            continue;
                        }
                        responseLength = range.To!.Value - range.From!.Value + 1;
                        if (response.Content.Headers.ContentLength is { } length && length != responseLength)
                            throw new HttpRequestException("The audio source range length was inconsistent.", null, response.StatusCode);
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (response.Content.Headers.ContentRange is not null)
                            throw new HttpRequestException("The audio source returned a range for a full response.", null, response.StatusCode);
                        // The server ignored Range, or If-Range no longer matched: replace the whole file.
                        if (checkpoint is not null) await checkpoint.ClearAsync(cancellationToken);
                        ResetPartial();
                        totalLength = response.Content.Headers.ContentLength;
                        strongTag = StrongTag(response);
                        responseLength = totalLength;
                    }
                    else
                        throw new HttpRequestException("The audio source did not return a complete or requested range response.", null, response.StatusCode);

                    if (totalLength > maxBytes || responseLength > maxBytes - offset)
                        throw new SourceDownloadTooLargeException();

                    await using (var file = new FileStream(audioPath, offset == 0 ? FileMode.Create : FileMode.Open,
                        checkpoint is null ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None, BufferSize, useAsync: true))
                    {
                        if (offset > 0)
                        {
                            if (file.Length != offset)
                                throw new SourceUnavailableException("The audio attempt file changed during recovery.");
                            file.Position = offset;
                        }
                        await using var source = await OpenBodyAsync(response, cancellationToken);
                        var buffer = new byte[BufferSize];
                        long received = 0;
                        while (true)
                        {
                            var read = await ReadBodyAsync(source, buffer, cancellationToken);
                            if (read == 0) break;
                            if (read > maxBytes - offset)
                                throw new SourceDownloadTooLargeException();
                            if (responseLength is { } expected && read > expected - received)
                                throw new HttpRequestException("The audio source body exceeded its declared length.", null, response.StatusCode);
                            // Disk failures propagate as IO failures, rather than being retried as network failures.
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            received += read;
                            offset += read;
                            if (checkpoint is not null)
                                await checkpoint.PublishAsync(file, current, strongTag, totalLength, offset, maxBytes, cancellationToken);
                        }
                        if (responseLength is { } expectedLength && received != expectedLength)
                            throw new IncompleteBodyException();
                        await file.FlushAsync(cancellationToken);
                    }

                    await using var completed = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        BufferSize, useAsync: true);
                    var hash = await SHA256.HashDataAsync(completed, cancellationToken);
                    return (current, Convert.ToHexString(hash).ToLowerInvariant(), offset);
                }
                catch (HttpRequestException exception) when (!refreshed && refresh is not null
                    && exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    refreshed = true;
                    current = await refresh(current, cancellationToken);
                    if (checkpoint is not null) await checkpoint.ClearAsync(cancellationToken);
                    ResetPartial();
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsRecoverable(exception))
                {
                    await RecoverAsync((exception as HttpRequestException)?.StatusCode);
                }
            }
        }
        catch (HttpRequestException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Request exceptions may contain signed URLs. Keep only the HTTP status at the task boundary.
            throw new HttpRequestException("The audio source request failed.", null, exception.StatusCode);
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is IncompleteBodyException
        or OperationCanceledException
        || exception is HttpRequestException { StatusCode: null }
        || exception is HttpRequestException request
            && (request.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int?)request.StatusCode is >= 500 and <= 599);

    private static string? StrongTag(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false, Tag: not "*" } tag ? tag.ToString() : null;

    private static async Task<HttpResponseMessage> OpenResponseAsync(HttpClient client, SourceDownloadInfo candidate,
        long resumeAt, string? strongTag, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidate.DownloadUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SourceUnavailableException("The audio candidate URL is invalid.");
        if (candidate.UseIsolatedDownloadClient && !PublicAudioHttpTransport.IsAllowedUri(candidate.DownloadUrl))
            throw new SourceUnavailableException("The audio candidate URL must refer to a public HTTP endpoint.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(request, candidate.RequestHeaders);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (resumeAt > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeAt, null);
            request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(strongTag!));
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        if (headers.Count > 8)
            throw new SourceUnavailableException("The audio candidate contains too many request headers.");
        foreach (var (name, value) in headers)
        {
            var allowed = name.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Origin", StringComparison.OrdinalIgnoreCase);
            if (!allowed || value is null || value.Length > 4096 || value.Contains('\r') || value.Contains('\n')
                || !request.Headers.TryAddWithoutValidation(name, value))
                throw new SourceUnavailableException("The audio candidate contains an unsupported request header.");
        }
    }

    private static async Task<Stream> OpenBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try { return await response.Content.ReadAsStreamAsync(cancellationToken); }
        catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); throw new IncompleteBodyException(); }
    }

    private static async ValueTask<int> ReadBodyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { return await source.ReadAsync(buffer, cancellationToken); }
        catch (IOException) { cancellationToken.ThrowIfCancellationRequested(); throw new IncompleteBodyException(); }
    }

    private sealed class IncompleteBodyException : Exception;
}
