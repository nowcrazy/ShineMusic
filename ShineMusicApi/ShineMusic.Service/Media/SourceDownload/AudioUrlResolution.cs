using ShineMusic.Model.Options;

namespace ShineMusic.Service.SourceDownload;

/// <summary>一次可独立重试的音频地址解析；身份和音质不随临时 URL 变化。</summary>
public sealed record AudioUrlResolveRequest(
    string Provider,
    string ProviderTrackId,
    string Level,
    string ResolverId,
    HttpAudioUrlEndpointOptions Endpoint,
    int TimeoutSeconds,
    string HttpClientName = HttpAudioUrlResolver.HttpClientName,
    Action<AudioUrlResolveDiagnostic>? Diagnostic = null);

/// <summary>不含地址、响应正文或凭据的解析诊断。</summary>
public sealed record AudioUrlResolveDiagnostic(string Stage, string Code, int? HttpStatus = null);

/// <summary>把平台曲目及音质解析为可下载候选，不负责下载或判定实际音质。</summary>
public interface IAudioUrlResolver
{
    Task<SourceDownloadInfo?> ResolveAsync(AudioUrlResolveRequest request, CancellationToken cancellationToken);
}
