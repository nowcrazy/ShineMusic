// 职责：按业务模块分组的 DI 注册。
// 原先 621 行的 ServiceCollectionExtensions 把六个模块的注册混在一个方法里；
// 这里按「纵向模块」拆开，ServiceCollectionExtensions 只保留总装入口。
// 注意：IEnumerable<T> 的解析顺序即注册顺序（歌词源兜底链、音源 provider 顺序、歌单解析器顺序），
// 因此同一接口的多个实现必须留在同一个模块方法内，不得跨模块拆分。

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Options;
using ShineMusic.Model.Ports;
using ShineMusic.Repository.Data;
using ShineMusic.Repository.Persistence;
using ShineMusic.Repository.Storage;
using ShineMusic.Service;
using ShineMusic.Service.Application;
using ShineMusic.Service.Lyrics;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;

namespace ShineMusic.Api.Extensions;

/// <summary>按业务能力分组的服务注册。</summary>
public static class ModuleRegistrations
{
    /// <summary>基础设施端口与媒体处理实现：换存储/换转码器只改这一处。</summary>
    public static IServiceCollection AddShineMusicInfrastructurePorts(this IServiceCollection services)
    {
        // 存储路径定位、转码/元数据服务。
        services.AddSingleton<LibraryStoragePaths>();
        services.AddSingleton<QuarantineStoragePaths>();
        services.AddSingleton<FfmpegMediaService>();
        services.AddSingleton<AudioMetadataParser>();
        services.AddSingleton<ArtworkFetcher>();
        // 领域端口 → 基础设施实现：用例层只依赖 Model.Ports 中的接口。
        services.AddSingleton<IAudioStorage, LocalAudioStorage>();
        services.AddSingleton<IMediaTranscoder, MediaTranscoderAdapter>();
        services.AddScoped<ITrackPlaybackStore, SqlSugarTrackPlaybackStore>();
        services.AddScoped<IArtworkVisibilityStore, SqlSugarArtworkVisibilityStore>();
        // Application 层用例：接口层通过它们表达用例，不再直接注入 ISqlSugarClient。
        services.AddScoped<ArtworkReadUseCase>();
        services.AddScoped<RecommendationStrategyUseCase>();
        services.AddScoped<ImportStagingService>();
        services.AddScoped<ImportPublishService>();
        return services;
    }

    /// <summary>Metadata 模块：歌词聚合、外部歌单解析与导入。</summary>
    public static IServiceCollection AddShineMusicMetadataModule(this IServiceCollection services)
    {
        // 歌词解析器与外部源提供者链。
        services.AddHttpClient(LyricsResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IExternalLyricsProvider, LrclibLyricsProvider>();
        services.AddSingleton<IExternalLyricsProvider, KuGouLyricsProvider>();
        services.AddSingleton<IExternalLyricsProvider, MiguLyricsProvider>();
        services.AddSingleton<ILyricsResolver, LyricsResolver>();

        // ===== 外部歌单解析导入 =====
        // 注：命名客户端 "netease" 由音源 provider 与歌单解析器共享；
        // 可选登录态 Cookie：配置后 provider 可取 VIP 曲目直链；匿名态（默认空）行为与之前完全一致。
        services.AddHttpClient(NeteasePlaylistParser.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Netease.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        });
        services.AddHttpClient(QQMusicPlaylistParser.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
        });
        // 汽水：分享页/短链解析 + 下载直链（task.SourceProvider="qishui" 复用该客户端），整曲体积大放宽到 120s。
        services.AddHttpClient(QishuiPlaylistParser.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://music.douyin.com/qishui/");
        });
        services.AddSingleton<IPlaylistParser, NeteasePlaylistParser>();
        services.AddSingleton<IPlaylistParser, QQMusicPlaylistParser>();
        services.AddSingleton<IPlaylistParser, KuwoPlaylistParser>();
        services.AddSingleton<IPlaylistParser, KugouPlaylistParser>();
        services.AddSingleton<IPlaylistParser, QishuiPlaylistParser>();
        services.AddSingleton<IPlaylistParser, YtDlpPlaylistParser>();
        // 解析结果缓存（单例）：预览与导入复用同一次平台抓取，避免导入时二次解析大歌单。
        services.AddSingleton<PlaylistParseCache>();
        services.AddScoped<PlaylistImportService>();
        return services;
    }

    /// <summary>Media 模块：音源适配、下载任务、存储治理与发布流水线。</summary>
    public static IServiceCollection AddShineMusicMediaModule(this IServiceCollection services)
    {
        services.AddScoped<StorageGovernanceService>();

        // ===== 外部音源抓歌：命名 HttpClient + provider 集合 =====
        // ===== 外部音源抓歌：命名 HttpClient + provider 集合 =====
        services.AddHttpClient(AudiusSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(JamendoSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(YouTubeMusicSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(KuwoSourceProvider.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            // 可选登录态 Cookie：配置后取 VIP/320k 资源；匿名态（默认空）行为与之前完全一致。
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Kuwo.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        });
        // 酷狗：plist/getSongInfo 系接口要求移动端 UA（缺省返回非 JSON），下载直链 sharefs 无特殊要求。
        // 全局频控网关 KugouRateLimitHandler：令牌桶限流（默认 2 QPS），防止触发酷狗 errcode:1002「操作太频繁」封禁。
        services.AddHttpClient(KugouSourceProvider.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1");
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Kugou.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        })
        // 酷狗对带 Accept-Encoding: gzip/deflate 的请求返回空 lists（total=0），
        // 故禁用 SocketsHttpHandler 的自动解压，避免其自动注入 Accept-Encoding 头。
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        .AddHttpMessageHandler<KugouRateLimitHandler>();
        services.AddTransient<KugouRateLimitHandler>();
        // 咪咕：显式用 SocketsHttpHandler 限定 TLS1.2/1.3 并关闭 Expect: 100-continue。
        services.AddHttpClient(MiguSourceProvider.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.ExpectContinue = false;
            // 可选登录态 Cookie：配置后取 VIP 资源；匿名态（默认空）行为与之前完全一致。
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Migu.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        })
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            },
            PooledConnectionLifetime = TimeSpan.FromSeconds(60),
        });
        services.AddSingleton<IMusicSourceProvider, AudiusSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, JamendoSourceProvider>();
        services.AddSingleton<YtDlpBridge>();
        services.AddSingleton<IMusicSourceProvider, YouTubeMusicSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, KuwoSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, KugouSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, MiguSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, NeteaseSourceProvider>();
        // 汽水音乐：搜索需 Android 客户端 UA、详情需浏览器 UA，因此不设默认 UA，由 provider 逐请求指定；
        // 可选登录 Cookie 供后续解锁完整授权信息。下载直链走 task.SourceProvider="qishui"（与歌单解析器同名客户端）。
        services.AddHttpClient(QishuiSourceProvider.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Qishui.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        });
        services.AddSingleton<IMusicSourceProvider, QishuiSourceProvider>();
        // QQ音乐：搜索走 c.y.qq.com、vkey 取流走 u.y.qq.com，浏览器 UA；
        // 可选登录态 Cookie：配置后取 VIP/320k/无损资源；匿名态（默认空）仅 128k m4a。
        services.AddHttpClient(QqSourceProvider.HttpClientName, (serviceProvider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            var cookie = serviceProvider.GetRequiredService<IOptions<SourceDownloadOptions>>().Value.Qq.Cookie;
            if (!string.IsNullOrWhiteSpace(cookie))
                client.DefaultRequestHeaders.Add("Cookie", cookie.Trim());
        });
        // 千千音乐（91q）：公开 API 免登录，需浏览器 UA + Referer + 自定义头 from:web（缺省签名接口 403/-400）；
        // 服务端无视 Accept-Encoding 恒返回 gzip，handler 必须开自动解压。
        services.AddHttpClient(QianqianSourceProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://music.91q.com/player");
            client.DefaultRequestHeaders.Add("from", "web");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        });
        // Bilibili：搜索/详情 API 必须浏览器 UA + Referer（缺省返回 -412 风控）；下载走 yt-dlp 桥接（ISourceFileDownloader），不经此客户端。
        services.AddHttpClient(BilibiliSourceProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
        });
        services.AddSingleton<IMusicSourceProvider, QqSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, QianqianSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, BilibiliSourceProvider>();
        services.AddScoped<SourceDownloadService>();
        services.AddScoped<SourceDownloadFallbackService>();
        return services;
    }

    /// <summary>Library 模块：曲库查询编排、推荐策略与推荐服务。</summary>
    public static IServiceCollection AddShineMusicLibraryModule(this IServiceCollection services)
    {
        services.AddScoped<RecommendationStrategyService>();
        services.AddScoped<RecommendationService>();
        return services;
    }

    /// <summary>Identity 模块：用户、令牌与邀请邮件。</summary>
    public static IServiceCollection AddShineMusicIdentityModule(this IServiceCollection services)
    {
        // ===== 用户/鉴权服务 =====
        // ===== 用户/鉴权服务 =====
        services.AddScoped<UserService>();
        services.AddSingleton<TokenService>();
        services.AddTransient<IInvitationEmailSender, InvitationEmailSender>();
        return services;
    }

    /// <summary>Admin 模块：消息队列监控等运维能力。</summary>
    public static IServiceCollection AddShineMusicAdminModule(this IServiceCollection services)
    {
        services.AddScoped<MessageQueueStatusService>();
        // RabbitMQ Management HTTP API 客户端（管理后台队列监控）：BaseAddress + Basic 认证随 RabbitMQ 配置走。
        // RabbitMQ Management HTTP API 客户端（管理后台队列监控）：BaseAddress + Basic 认证随 RabbitMQ 配置走。
        services.AddHttpClient(MessageQueueStatusService.HttpClientName, (serviceProvider, client) =>
        {
            var rabbit = serviceProvider.GetRequiredService<IOptions<RabbitOptions>>().Value;
            client.BaseAddress = new Uri($"http://{rabbit.HostName}:{rabbit.ManagementPort}/");
            client.Timeout = TimeSpan.FromSeconds(5);
            var credential = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{rabbit.UserName}:{rabbit.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credential);
        });
        return services;
    }
}
