// 客户端识别：从请求头提取客户端类型与设备描述，供登录/操作审计使用。
// - 优先读取显式头 X-Client-Type（web-admin / app / web-app），缺失时按 User-Agent 粗判；
// - 设备描述：PC（浏览器）解析 UA 得浏览器+系统，App 优先取 X-Device-Info（客户端上报机型/系统），
//   未上报时回退 UA 片段（安卓 WebView 的 UA 含机型）。

namespace ShineMusic.Service;

/// <summary>请求客户端画像：类型 + 设备描述。</summary>
public static class ClientInfo
{
    /// <summary>从 HTTP 上下文解析客户端类型与设备描述。</summary>
    public static (string ClientType, string DeviceInfo) Resolve(HttpContext context)
    {
        var headers = context.Request.Headers;
        var ua = Truncate(headers.UserAgent.ToString(), 512);
        var declared = headers["X-Client-Type"].ToString().Trim().ToLowerInvariant();

        var clientType = declared is "web-admin" or "app" or "web-app"
            ? declared
            : InferClientType(ua);
        var deviceInfo = ResolveDeviceInfo(headers, ua, clientType);
        return (clientType, deviceInfo);
    }

    /// <summary>取客户端 IP（经代理时优先 X-Forwarded-For 首跳）。</summary>
    public static string ResolveIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return Truncate(forwarded.Split(',')[0].Trim(), 64);
        return Truncate(context.Connection.RemoteIpAddress?.ToString() ?? "-", 64);
    }

    private static string InferClientType(string ua)
    {
        if (ua.Length == 0) return "unknown";
        // uni-app 原生 WebView 的 UA 一般含 Html5Plus 或 uni-app 标记；手机 UA 判为 app。
        if (ua.Contains("Html5Plus", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("uni-app", StringComparison.OrdinalIgnoreCase))
            return "app";
        if (ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("X11", StringComparison.OrdinalIgnoreCase))
            return "web-admin";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
            return "app";
        return "unknown";
    }

    private static string ResolveDeviceInfo(IHeaderDictionary headers, string ua, string clientType)
    {
        // App 端显式上报机型（如 "Xiaomi 14 · Android 14"）最可靠，优先采用。
        var reported = headers["X-Device-Info"].ToString().Trim();
        if (reported.Length > 0) return Truncate(reported, 256);

        if (clientType is "web-admin" or "web-app")
            return Truncate(ParseBrowser(ua), 256);
        return Truncate(ParseMobile(ua), 256);
    }

    /// <summary>从 UA 提取 "浏览器 · 操作系统"（桌面端）。</summary>
    private static string ParseBrowser(string ua)
    {
        if (ua.Length == 0) return "unknown";
        var os = ua.Contains("Windows NT 10", StringComparison.OrdinalIgnoreCase) ? "Windows 10/11"
            : ua.Contains("Windows NT", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : ua.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : "其他系统";
        var browser = ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : ua.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera"
            : ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "其他浏览器";
        return $"{browser} · {os}";
    }

    /// <summary>从 UA 提取移动端设备信息（机型 + 系统）。</summary>
    private static string ParseMobile(string ua)
    {
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone · iOS";
        if (ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad · iPadOS";
        var android = ua.IndexOf("Android", StringComparison.OrdinalIgnoreCase);
        if (android < 0) return ua.Length > 0 ? ua[..Math.Min(ua.Length, 120)] : "unknown";
        // 安卓 UA 形如 "...; Android 14; M2012K11AC Build/..."。
        var versionEnd = ua.IndexOf(';', android);
        var version = versionEnd > android ? ua[android..versionEnd].Trim() : "Android";
        var model = string.Empty;
        if (versionEnd > 0)
        {
            var modelEnd = ua.IndexOfAny([';', ')'], versionEnd + 1);
            if (modelEnd > versionEnd + 1)
                model = ua[(versionEnd + 1)..modelEnd].Replace("Build/", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        return model.Length > 0 ? $"{model} · {version}" : version;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
