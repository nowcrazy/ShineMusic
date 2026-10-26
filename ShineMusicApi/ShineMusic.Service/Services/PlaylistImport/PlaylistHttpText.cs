// 职责：歌单解析平台的 HTTP 文本抓取辅助。
// 部分平台（如网易云）响应头声明 charset=utf8（无连字符），
// .NET 的 Encoding.GetEncoding 不识别该别名，HttpClient.GetStringAsync 会抛
// InvalidOperationException；因此这里统一读原始字节并按 UTF-8 解码，
// 并把网络/超时异常归一化为对用户可见的 PlaylistParseException。

namespace ShineMusic.Service.PlaylistImport;

/// <summary>以 UTF-8 解码抓取远程文本，规避非常规 charset 别名导致的解码异常。</summary>
internal static class PlaylistHttpText
{
    /// <summary>GET 指定 URL，读取字节后按 UTF-8 解码为字符串。</summary>
    public static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await client.GetByteArrayAsync(url, cancellationToken);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (HttpRequestException)
        {
            throw new PlaylistParseException("无法连接目标音乐平台，请稍后重试。");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlaylistParseException("目标音乐平台响应超时，请稍后重试。");
        }
    }
}
