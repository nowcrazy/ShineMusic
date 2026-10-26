// 应用程序入口：组装依赖注入容器（选项校验、PostgreSQL 数据库、Redis 缓存、RabbitMQ 消息队列、JWT、CORS 与应用服务）、
// 配置 HTTP 中间件管道与端点映射；启动时由 SqlSugar CodeFirst 幂等建表并播种角色，
// 另支持以 --create-admin 命令行参数交互式创建首位管理员（创建后直接退出，不启动 Web 服务）。
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using ShineMusic.Model.Entities;
using ShineMusic.Repository.Data;
using ShineMusic.Api.Controllers;
using ShineMusic.Common.Logging;
using ShineMusic.Model.Options;
using ShineMusic.Service;
using ShineMusic.Model.Contracts;
using Microsoft.OpenApi;
using SqlSugar;

var builder = WebApplication.CreateBuilder(args);
// Kestrel 请求体上限放宽到 4 GiB（4294967296 字节）：这是整批无损音频上传需要的理论上限，
// 单文件/单批次真正生效的大小仍由 Uploads 配置节在业务层校验；此处只保证服务器最外层不会提前拒绝大请求。
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4294967296);

// ===== 全局地址配置：仓库根目录 config/hosts.json 为唯一来源 =====
// 前端构建（vite.config.ts）与后端启动都读这一份；改 IP / 域名只改它，两边自动同步。
// 用独立 PhysicalFileProvider 挂载仓库根目录，绕开「配置只能读内容根目录内文件」的限制。
var repoRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".."));
var hostsRelativePath = Path.Combine("config", "hosts.json");
if (File.Exists(Path.Combine(repoRoot, hostsRelativePath)))
{
    builder.Configuration.AddJsonFile(
        new Microsoft.Extensions.FileProviders.PhysicalFileProvider(repoRoot),
        hostsRelativePath,
        optional: true,
        reloadOnChange: false);
}

// ===== 文件日志：按分类（operations/auth/errors/debug/data/app）与日期分离落盘 =====
builder.Logging.AddFileLogger(options =>
{
    // 相对路径按内容根目录解析为绝对路径，避免随工作目录漂移。
    options.LogsRoot = Path.Combine(builder.Environment.ContentRootPath, "logs");
});

// ===== 基础服务：OpenAPI/Swagger 文档与 MVC 控制器 =====
// 使用 Swashbuckle 提供可交互的 Swagger UI（/swagger），并预置 JWT Bearer 鉴权按钮。
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "ShineMusic API",
        Version = "v1",
        Description = "ShineMusic 音乐服务接口"
    });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "输入 JWT（含 Bearer 前缀），例如：Bearer <your-access-token>"
    });
    // 说明：Microsoft.OpenApi 2.x 将 OpenApiSecurityRequirement 的键类型改为
    // OpenApiSecuritySchemeReference（无公开无参构造），全局安全需求无法用旧写法拼装；
    // 改为按操作注入：只有带 [Authorize] 的端点声明 Bearer 要求，Swagger UI 才会把
    // Authorize 里粘贴的令牌带进请求，匿名端点不受影响。
    c.OperationFilter<ShineMusic.Api.OpenApi.BearerSecurityOperationFilter>();
});
builder.Services.AddControllers();
// ===== 统一 400 错误体（契约一致性） =====
// [ApiController] 默认会在模型绑定失败（JSON 语法错误、字段类型不匹配、缺失必填项）时
// 直接返回 RFC 7807 的 application/problem+json，字段是 type/title/errors，
// 与全站约定的 { "error": { "code", "message", "traceId" } } 不是同一套结构。
// 前端 services/http.ts 只解析后者，遇到前者会把真实原因压成「请求失败：400」并丢掉 traceId。
// 这里接管默认工厂，把多条校验错误折叠成第一条，包装成自定义错误体。
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var detail = string.Join('；', context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .Select(entry =>
            {
                var message = entry.Value!.Errors[0].ErrorMessage;
                return string.IsNullOrEmpty(entry.Key) || entry.Key == "$"
                    ? message
                    : $"{entry.Key}: {message}";
            }));
        return new BadRequestObjectResult(new ApiErrorResponse(
            new ApiErrorDetail(
                ErrorCodes.ValidationError,
                string.IsNullOrWhiteSpace(detail) ? "请求参数不合法。" : detail,
                context.HttpContext.TraceIdentifier)));
    };
});
builder.Services.AddHttpClient();
// ===== 响应压缩：对 JSON 等文本响应启用 gzip/brotli，减小传输体积（客户端未声明支持时自动跳过） =====
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json", "text/json" });
});
// ===== 限流：对行为上报等写接口按用户（匿名按 IP）做固定窗口限流，抵御刷量 =====
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("play-events", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? httpContext.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,                 // 每分钟最多 120 条上报（约 2 条/秒，远超正常播放频率）
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0                     // 超限直接拒绝，不排队
            }));
});
// ===== 选项校验：强类型对象绑定到对应配置节，ValidateOnStart 让配置错误在启动阶段直接失败，而非拖到首个请求才暴露 =====
builder.Services
    .AddOptions<DatabaseOptions>()
    .BindConfiguration(DatabaseOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Database:ConnectionString is required.")
    .ValidateOnStart();
builder.Services
    .AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.LibraryRoot), "Storage:LibraryRoot is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.QuarantineRoot), "Storage:QuarantineRoot is required.")
    .ValidateOnStart();
builder.Services
    .AddOptions<AuthOptions>()
    .BindConfiguration(AuthOptions.SectionName)
    .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32, "Auth:SigningKey must be configured with at least 32 UTF-8 bytes.")
    .Validate(options => options.AccessTokenMinutes is >= 1 and <= 60, "Auth:AccessTokenMinutes must be between 1 and 60.")
    .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "Auth:RefreshTokenDays must be between 1 and 90.")
    .Validate(options => options.InvitationExpiryHours is >= 1 and <= 720, "Auth:InvitationExpiryHours must be between 1 and 720.")
    .ValidateOnStart();
builder.Services.Configure<InvitationEmailOptions>(builder.Configuration.GetSection(InvitationEmailOptions.SectionName));
builder.Services
    .AddOptions<RabbitOptions>()
    .BindConfiguration(RabbitOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.HostName), "RabbitMQ:HostName is required.")
    .ValidateOnStart();
// 密码强度门槛（仅用于用户主动「修改密码」环节，不作用于 --create-admin 首位管理员创建）。
builder.Services
    .AddOptions<PasswordPolicyOptions>()
    .BindConfiguration(PasswordPolicyOptions.SectionName)
    .Validate(options => options.MinimumStrength is >= 0 and <= 4, "PasswordPolicy:MinimumStrength must be between 0 and 4.")
    .ValidateOnStart();
// 邀请邮件里的注册链接：appsettings 未显式配置时，由 config/hosts.json 按环境推导，
// 避免同一个 IP 在多个文件里重复维护。
builder.Services.PostConfigure<InvitationEmailOptions>(options =>
{
    if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return;
    var lanIp = builder.Configuration["local:lanIp"];
    var webPort = builder.Configuration["local:webPort"] ?? "5173";
    options.PublicBaseUrl = builder.Environment.IsDevelopment()
        ? (string.IsNullOrWhiteSpace(lanIp) ? $"http://localhost:{webPort}" : $"http://{lanIp}:{webPort}")
        : builder.Configuration["server:webOrigin"] ?? string.Empty;
});
builder.Services
    .AddOptions<FeatureOptions>()
    .BindConfiguration(FeatureOptions.SectionName)
    .Validate(options => options.ManualReview, "Features:ManualReview must be enabled for the first release.")
    .Validate(options => options.ServerTranscoding, "Features:ServerTranscoding must be enabled for the first release.")
    .ValidateOnStart();
// 点歌满足流程的合规校验配置：核心检查 TitleArtistMatch 必须存在，避免配置缺失时静默放过错配。
builder.Services
    .AddOptions<SongRequestFulfillmentOptions>()
    .BindConfiguration(SongRequestFulfillmentOptions.SectionName)
    .Validate(options => options.Checks.ContainsKey("TitleArtistMatch"), "SongRequestFulfillment:Checks:TitleArtistMatch is required.")
    .Validate(options => options.TitleArtistMatch.MaxEditDistance is >= 0 and <= 5, "SongRequestFulfillment:TitleArtistMatch:MaxEditDistance must be between 0 and 5.")
    .ValidateOnStart();
// 点歌需求运营配置：自动归档保留期与扫描间隔。
builder.Services
    .AddOptions<SongRequestOptions>()
    .BindConfiguration(SongRequestOptions.SectionName)
    .Validate(options => options.ArchiveAfterDays is >= 1 and <= 3650, "SongRequests:ArchiveAfterDays must be between 1 and 3650.")
    .Validate(options => options.ArchiveScanIntervalMinutes is >= 1 and <= 1440, "SongRequests:ArchiveScanIntervalMinutes must be between 1 and 1440.")
    .ValidateOnStart();
builder.Services
    .AddOptions<UploadOptions>()
    .BindConfiguration(UploadOptions.SectionName)
    .Validate(options => options.MaxFileSizeBytes is > 0 and <= 1073741824, "Uploads:MaxFileSizeBytes must be between 1 byte and 1 GiB.")
    .Validate(options => options.MaxBatchSizeBytes >= options.MaxFileSizeBytes, "Uploads:MaxBatchSizeBytes must be at least MaxFileSizeBytes.")
    .Validate(options => options.MaxBatchSizeBytes <= 4294967296, "Uploads:MaxBatchSizeBytes must not exceed 4 GiB.")
    .Validate(options => options.MaxFilesPerBatch is >= 1 and <= 20, "Uploads:MaxFilesPerBatch must be between 1 and 20.")
    .Validate(options => options.AllowedFormats.Count > 0, "Uploads:AllowedFormats must contain at least one format.")
    .ValidateOnStart();
// multipart 表单解析层的体积上限同样放宽到 4 GiB，与 Kestrel 上限对齐，避免表单解析先于业务校验抛出异常。
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 4294967296);
builder.Services
    .AddOptions<TranscodingOptions>()
    .BindConfiguration(TranscodingOptions.SectionName)
    .Validate(options => options.TargetFormat == "aac_m4a", "Only the configured aac_m4a output is currently supported.")
    .Validate(options => options.BitrateKbps is >= 96 and <= 320, "Transcoding:BitrateKbps must be between 96 and 320.")
    .Validate(options => options.MaxConcurrentJobs is >= 1 and <= 4, "Transcoding:MaxConcurrentJobs must be between 1 and 4.")
    .ValidateOnStart();
builder.Services.Configure<FfmpegOptions>(builder.Configuration.GetSection(FfmpegOptions.SectionName));
// 歌词自动匹配：外部源调用参数，配置错误在启动阶段直接暴露。
builder.Services
    .AddOptions<LyricsOptions>()
    .BindConfiguration(LyricsOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Lyrics:BaseUrl is required.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 30, "Lyrics:TimeoutSeconds must be between 1 and 30.")
    .Validate(options => options.MinScore is >= 0 and <= 1, "Lyrics:MinScore must be between 0 and 1.")
    .ValidateOnStart();
builder.Services
    .AddOptions<LibraryImportOptions>()
    .BindConfiguration(LibraryImportOptions.SectionName)
    .Validate(options => options.AutoPublishThreshold is >= 0 and <= 100,
        "LibraryImport:AutoPublishThreshold must be between 0 and 100.")
    .ValidateOnStart();
builder.Services
    .AddOptions<SourceDownloadOptions>()
    .BindConfiguration(SourceDownloadOptions.SectionName)
    .Validate(options => !options.Audius.Enabled || !string.IsNullOrWhiteSpace(options.Audius.BaseUrl),
        "SourceDownload:Audius:BaseUrl is required when Audius is enabled.")
    .Validate(options => !options.Jamendo.Enabled || !string.IsNullOrWhiteSpace(options.Jamendo.ClientId),
        "SourceDownload:Jamendo:ClientId is required when Jamendo is enabled.")
    .Validate(options => options.AutoPublishThreshold is >= 0 and <= 100,
        "SourceDownload:AutoPublishThreshold must be between 0 and 100.")
    .Validate(options => options.Download.MaxConcurrentDownloads is >= 1 and <= 4,
        "SourceDownload:Download:MaxConcurrentDownloads must be between 1 and 4.")
    .Validate(options => options.YtDlp.SearchTimeoutSeconds is >= 10 and <= 300,
        "SourceDownload:YtDlp:SearchTimeoutSeconds must be between 10 and 300.")
    .Validate(options => options.YtDlp.MetadataTimeoutSeconds is >= 5 and <= 120,
        "SourceDownload:YtDlp:MetadataTimeoutSeconds must be between 5 and 120.")
    .ValidateOnStart();
// ===== CORS：优先使用 appsettings 显式配置；未配置时由 config/hosts.json 推导当前环境的前端来源 =====
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0)
{
    var lanIp = builder.Configuration["local:lanIp"];
    var webPort = builder.Configuration["local:webPort"] ?? "5173";
    var serverWebOrigin = builder.Configuration["server:webOrigin"];
    allowedOrigins = builder.Environment.IsDevelopment()
        ? (string.IsNullOrWhiteSpace(lanIp)
            ? [$"http://localhost:{webPort}", $"http://127.0.0.1:{webPort}"]
            : [$"http://localhost:{webPort}", $"http://127.0.0.1:{webPort}", $"http://{lanIp}:{webPort}"])
        // "null"：5+App/HBuilderX 本地壳的页面来源是 file://，浏览器规范规定其跨域请求 Origin 头为字面量 "null"
        : (string.IsNullOrWhiteSpace(serverWebOrigin) ? [] : [serverWebOrigin, "null"]);
}
builder.Services.AddCors(options => options.AddPolicy("Client", policy =>
{
    // 未配置任何来源时不放行任何跨域请求（失败即关闭），避免开发期宽松配置被误带到生产环境。
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

// ===== 数据库：PostgreSQL（SqlSugar）=====
var configuredConnectionString = builder.Configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];

// 数据库访问按作用域注册：每个 HTTP 请求、每条队列消息、每轮定时任务各持一个 SqlSugarClient。
// 曾用 SqlSugarScope 单例（内部按异步上下文切换连接），但在后台任务与消息消费者并发时偶发
// 「Connection already open」——作用域隔离后各链路连接互不影响。实际 SQL 仍由工厂内 AOP 写入 logs/data。
builder.Services.AddScoped<SqlSugarClient>(serviceProvider =>
{
    // 显式解析一次选项对象以触发 DatabaseOptions 的启动校验，确保连接字符串在建连前已通过验证。
    _ = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    return SqlSugarFactory.Create(configuredConnectionString!, serviceProvider.GetRequiredService<ILoggerFactory>());
});
builder.Services.AddScoped<ISqlSugarClient>(serviceProvider => serviceProvider.GetRequiredService<SqlSugarClient>());

// ===== Redis：分布式缓存（IDistributedCache）与连接多路复用器 =====
// Redis:ConnectionString 留空时回退进程内内存缓存，保证无 Docker 的临时开发环境仍可启动；
// 生产与正常开发一律走 docker compose 的 redis 服务（缓存跨重启保留、多端共享）。
var redisConnectionString = builder.Configuration[$"{RedisOptions.SectionName}:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisConnectionString))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = builder.Configuration[$"{RedisOptions.SectionName}:InstanceName"] ?? "shinemusic:";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// ===== RabbitMQ：持久化消息队列（下载/导入），业务状态机由 PostgreSQL 业务表承载 =====
builder.Services.AddSingleton<ShineMusic.Service.Messaging.RabbitMessageBus>();
builder.Services.AddSingleton<ShineMusic.Service.Messaging.IRabbitMessageBus>(sp => sp.GetRequiredService<ShineMusic.Service.Messaging.RabbitMessageBus>());
// 消费端（BackgroundService 常驻监听；两个具体子类走类型注册——同类型工厂注册会被 TryAddEnumerable 去重而跳过第二个）。
builder.Services.AddHostedService<ShineMusic.Tasks.SourceDownloadConsumerService>();
builder.Services.AddHostedService<ShineMusic.Tasks.LibraryImportConsumerService>();
// 周期任务定时器（歌词补跑 / 点歌归档 / 导入重扫 / 下载重扫）。
builder.Services.AddHostedService<ShineMusic.Tasks.LyricsBackfillService>();
builder.Services.AddHostedService<ShineMusic.Tasks.SongRequestArchiveService>();
builder.Services.AddHostedService<ShineMusic.Tasks.LibraryImportRetrySweepService>();
builder.Services.AddHostedService<ShineMusic.Tasks.SourceDownloadRetrySweepService>();
// 业务处理器注册（每次消息/每次定时器轮次独立作用域，可注入 scoped 服务）。
builder.Services.AddTransient<ShineMusic.Service.LibraryImportJob>();
builder.Services.AddTransient<ShineMusic.Service.LibraryImportRetrySweepJob>();
builder.Services.AddTransient<ShineMusic.Service.SourceDownload.SourceDownloadJob>();
builder.Services.AddTransient<ShineMusic.Service.SourceDownload.SourceDownloadRetrySweepJob>();
builder.Services.AddTransient<ShineMusic.Tasks.LyricsBackfillJob>();
builder.Services.AddTransient<ShineMusic.Tasks.SongRequestArchiveJob>();
// ===== 应用服务：存储路径定位、转码/元数据服务的生命周期注册 =====
builder.Services.AddSingleton<LibraryStoragePaths>();
builder.Services.AddSingleton<QuarantineStoragePaths>();
builder.Services.AddSingleton<FfmpegMediaService>();
builder.Services.AddSingleton<AudioMetadataParser>();
builder.Services.AddSingleton<ArtworkFetcher>();
builder.Services.AddScoped<ImportStagingService>();
// 导入条目发布核心逻辑：人工发布（管理端点）与音源评分直通（后台服务）共用，依赖 ISqlSugarClient 按作用域注册。
builder.Services.AddScoped<ImportPublishService>();
// 歌词解析器：无状态，注册为单例；命名 HttpClient 单独控制外部歌词源的连接与超时。
builder.Services.AddHttpClient(LyricsResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
// 外部歌词源提供者链（多源化）：按 LyricsOptions.ExternalSources 顺序兜底，全部单例无状态。
builder.Services.AddSingleton<ShineMusic.Service.Lyrics.IExternalLyricsProvider, ShineMusic.Service.Lyrics.LrclibLyricsProvider>();
builder.Services.AddSingleton<ShineMusic.Service.Lyrics.IExternalLyricsProvider, ShineMusic.Service.Lyrics.KuGouLyricsProvider>();
builder.Services.AddSingleton<ShineMusic.Service.Lyrics.IExternalLyricsProvider, ShineMusic.Service.Lyrics.MiguLyricsProvider>();
builder.Services.AddSingleton<ILyricsResolver, LyricsResolver>();
// 存储治理服务：依赖 ISqlSugarClient，按请求作用域注册。
builder.Services.AddScoped<StorageGovernanceService>();
// 每日推荐服务：依赖 ISqlSugarClient，按请求作用域注册。
builder.Services.AddScoped<RecommendationService>();
// ===== 外部音源抓歌：命名 HttpClient（平台统一超时）、provider 集合与任务编排服务 =====
builder.Services.AddHttpClient(ShineMusic.Service.SourceDownload.AudiusSourceProvider.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient(ShineMusic.Service.SourceDownload.JamendoSourceProvider.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(120));
// yt-dlp 系音源的封面下载走命名客户端；音频下载由 YtDlpBridge 子进程承担。
builder.Services.AddHttpClient(ShineMusic.Service.SourceDownload.YouTubeMusicSourceProvider.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddHttpClient(ShineMusic.Service.SourceDownload.SoundCloudSourceProvider.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(120));
// 酷我：HTTP 直链音源（搜索/详情/取直链均零鉴权）。
builder.Services.AddHttpClient(ShineMusic.Service.SourceDownload.KuwoSourceProvider.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.IMusicSourceProvider, ShineMusic.Service.SourceDownload.AudiusSourceProvider>();
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.IMusicSourceProvider, ShineMusic.Service.SourceDownload.JamendoSourceProvider>();
// yt-dlp 桥接（单例，进程内嵌管理 yt-dlp.exe）+ YouTube Music / SoundCloud 两个 provider。
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.YtDlpBridge>();
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.IMusicSourceProvider, ShineMusic.Service.SourceDownload.YouTubeMusicSourceProvider>();
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.IMusicSourceProvider, ShineMusic.Service.SourceDownload.SoundCloudSourceProvider>();
builder.Services.AddSingleton<ShineMusic.Service.SourceDownload.IMusicSourceProvider, ShineMusic.Service.SourceDownload.KuwoSourceProvider>();
builder.Services.AddScoped<ShineMusic.Service.SourceDownload.SourceDownloadService>();
// ===== 外部歌单解析导入：命名客户端（网易云需 UA/Referer 头，QQ 音乐需 Referer）+ 平台 parser 单例 + 编排服务 =====
builder.Services.AddHttpClient(ShineMusic.Service.PlaylistImport.NeteasePlaylistParser.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
});
builder.Services.AddHttpClient(ShineMusic.Service.PlaylistImport.QQMusicPlaylistParser.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
});
builder.Services.AddSingleton<ShineMusic.Service.PlaylistImport.IPlaylistParser, ShineMusic.Service.PlaylistImport.NeteasePlaylistParser>();
builder.Services.AddSingleton<ShineMusic.Service.PlaylistImport.IPlaylistParser, ShineMusic.Service.PlaylistImport.QQMusicPlaylistParser>();
builder.Services.AddSingleton<ShineMusic.Service.PlaylistImport.IPlaylistParser, ShineMusic.Service.PlaylistImport.KuwoPlaylistParser>();
builder.Services.AddSingleton<ShineMusic.Service.PlaylistImport.IPlaylistParser, ShineMusic.Service.PlaylistImport.YtDlpPlaylistParser>();
builder.Services.AddScoped<ShineMusic.Service.PlaylistImport.PlaylistImportService>();
// 歌词定时补跑、点歌需求自动归档、导入/下载失败重试扫描：由 BackgroundService 定时器驱动
// （启动恢复见 ShineMusic.Tasks.QueueBootstrap.InitializeAsync）。
// ===== 用户/角色服务：1b 阶段起由 SqlSugar 版 UserService 接管原 Identity UserManager/SignInManager/RoleManager 职责 =====
// 密码哈希仍为 Identity PBKDF2 格式（存量账号无需迁移）；密码策略/登录锁定/邮箱唯一等规则见 UserService 注释。
// 作用域注册：UserService 持有 ISqlSugarClient，不能作为单例被困在根作用域。
builder.Services.AddScoped<UserService>();
// ===== JWT：启动时密钥检查、令牌校验参数，以及 401/403 统一 JSON 错误响应 =====
var authConfiguration = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
// 在认证中间件构建前显式检查签名密钥：密钥缺失或不足 32 字节时直接拒绝启动，
// 避免服务带空/弱密钥运行，直到用户登录签发令牌时才暴露问题。
if (Encoding.UTF8.GetByteCount(authConfiguration.SigningKey) < 32)
    throw new InvalidOperationException("Set Auth__SigningKey in User Secrets or the server environment before starting the API.");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authConfiguration.SigningKey));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 保留 JWT 中原始的 claim 名称（如 sub），不让默认映射改写成旧式长 URI 名称。
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authConfiguration.Issuer,
            ValidateAudience = true,
            ValidAudience = authConfiguration.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            // 将默认 5 分钟的服务器间时钟偏移容差收紧到 30 秒，缩小过期令牌仍可用的窗口。
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            // <audio>/<video> 元素发请求时无法携带 Authorization 头，
            // 因此对试听/预览类接口允许通过 query string 传递 access_token。
            // 仅对已知的媒体流路径生效，避免把 query-token 鉴权扩散到其他接口。
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"].FirstOrDefault();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    var path = context.HttpContext.Request.Path.Value ?? string.Empty;
                    if (path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith("/stream", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Token = accessToken;
                    }
                }
                return Task.CompletedTask;
            },
            // 未携带/无效令牌访问受保护资源时，拦截默认质询逻辑，统一返回 ApiErrorResponse JSON。
            OnChallenge = async context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                    new ApiErrorDetail(ErrorCodes.AuthenticationRequired, "Authentication is required.", context.HttpContext.TraceIdentifier)));
            },
            // 已登录但权限/角色不足时，同样返回统一结构的 403 JSON。
            OnForbidden = async context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                    new ApiErrorDetail(ErrorCodes.PermissionDenied, "You do not have permission to access this resource.", context.HttpContext.TraceIdentifier)));
            }
        };
    });
builder.Services.AddAuthorization();
// 令牌签发服务与邀请邮件发送服务（SMTP 实现可替换）。
builder.Services.AddSingleton<TokenService>();
builder.Services.AddTransient<IInvitationEmailSender, InvitationEmailSender>();

var app = builder.Build();

// 启动快照：把环境、监听地址、日志目录记到 logs/app，重启后排查“跑的是哪个环境/哪份配置”时先看这行。
app.Logger.LogInformation(
    "ShineMusic API starting | Environment={Environment} | ContentRoot={ContentRoot} | Urls={Urls} | LogsRoot={LogsRoot}",
    app.Environment.EnvironmentName,
    app.Environment.ContentRootPath,
    builder.Configuration["Urls"] ?? "(default)",
    Path.Combine(app.Environment.ContentRootPath, "logs"));

// OpenAPI/Swagger 文档与可交互 UI 仅在开发环境暴露，生产环境不公开接口结构。
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ShineMusic API v1");
        c.RoutePrefix = "swagger"; // 访问根路径 /swagger 即可打开 UI
    });
    // RabbitMQ 无内置仪表盘；运维观测用 RabbitMQ Management UI（15672 端口，docker compose 已映射）。
}

// ===== 中间件管道（顺序敏感）：异常处理 → CORS → 认证 → 授权 → 端点路由 =====
// 未处理异常统一收敛为 500 JSON，先记录带 TraceId 的日志，再向客户端返回脱敏错误信息。
app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    // 客户端主动断连（TaskCanceledException / OperationCanceledException）：
    // ASP.NET Core 在客户端断开 HTTP 连接时会自动取消请求 CancellationToken，
    // 此时数据库/HTTP 请求等异步操作会抛出 TaskCanceledException。
    // 这不是服务端故障，不应该记录为 error 或返回 500，否则日志里会被大量噪音淹没。
    if (exception is OperationCanceledException)
    {
        context.Response.StatusCode = 499; // Client Closed Request（Nginx 约定状态码）
        return;
    }
    // 未处理异常统一收敛为 500 JSON，先记录带 TraceId 的日志（category 以 Errors 开头 → logs/errors），
    // 再向客户端返回脱敏错误信息。日志带方法/路径/TraceId，配合 operations/data/auth 日志即可顺
    // TraceId 还原整条请求链路。
    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Errors.UnhandledException")
        .LogError(exception, "Unhandled exception on {Method} {Path} (TraceId={TraceId}).",
            context.Request.Method, context.Request.Path, context.TraceIdentifier);
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await Results.Json(new ApiErrorResponse(
        new ApiErrorDetail(ErrorCodes.InternalError, "An unexpected error occurred.", context.TraceIdentifier)))
        .ExecuteAsync(context);
}));
// ===== 兜底错误体：把「没有响应体」的失败状态补齐成自定义错误体 =====
// 典型场景：路由未命中（404）、HTTP 方法不匹配（405）。此时 ASP.NET 只写状态码、Content-Type 为空、
// 响应体 0 字节，前端 http.ts 拿不到 code/message/traceId，只能显示「请求失败：404」。
// 判断条件是 ContentType 为空——业务 400/401/404/409 都已写入 JSON，此中间件不会重复介入。
app.Use(async (context, next) =>
{
    await next();
    var response = context.Response;

    // 只处理「失败了但又什么都没说」的情况；已开始写入响应或已声明内容类型的交给原逻辑。
    if (response.StatusCode < StatusCodes.Status400BadRequest) return;
    if (response.HasStarted) return;
    if (!string.IsNullOrEmpty(response.ContentType)) return;
    if (response.ContentLength is > 0) return;

    response.ContentType = "application/json";
    await response.WriteAsJsonAsync(new ApiErrorResponse(
        new ApiErrorDetail(
            ResolveStatusCodeCode(response.StatusCode),
            $"接口不存在或请求方式不被支持（HTTP {response.StatusCode}）。",
            context.TraceIdentifier)));
});

// 响应压缩放在管道靠前位置，使后续所有 JSON/文本响应都能被压缩。
app.UseResponseCompression();
// 显式启用路由：确保后续中间件（如限流）能读到端点元数据（[EnableRateLimiting] 等）。
app.UseRouting();
// CORS 必须先于认证/授权，认证先于授权，三者配合端点路由完成跨域与访问控制。
app.UseCors("Client");
// ===== 操作日志中间件：注册在认证之前，使「未携带/无效令牌」导致的 401/403 也被记录
// （否则鉴权短路会跳过本中间件，丢失最需要被定位的鉴权失败）。finally 中才读取 context.User，
// 此时下游认证已执行完毕，登录用户仍可取。 =====
app.UseMiddleware<ShineMusic.Common.Middleware.OperationLoggingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// 限流中间件：需在端点路由之前，[EnableRateLimiting] 标注的端点才会生效。
app.UseRateLimiter();

// ===== 端点映射：注册控制器路由（必须在认证/授权之后，否则鉴权中间件不生效） =====
app.MapControllers();

// ===== 启动数据准备：SqlSugar CodeFirst 幂等同步表结构（取代 EF 迁移），并确保 user、admin 两个内置角色已播种 =====
await using (var scope = app.Services.CreateAsyncScope())
{
    var sqlSugar = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
    ShineMusic.Repository.Data.SqlSugarSchemaBootstrap.Initialize(sqlSugar, app.Logger);
    var userService = scope.ServiceProvider.GetRequiredService<UserService>();
    // 内置角色幂等播种（替代原 RoleManager 逻辑）：user 普通用户 / admin 管理员 / tempadmin 临时管理员。
    foreach (var roleName in new[] { "user", "admin", "tempadmin" })
        await userService.EnsureRoleAsync(roleName);

    // —— 开发环境确保管理员账号可用（存在则解锁+重置密码；不存在则创建） ——
    var devAdminEmail = "3449352732@qq.com";
    var devAdmin = await userService.FindByEmailAsync(devAdminEmail);
    if (devAdmin is null)
    {
        devAdmin = new ApplicationUser { Email = devAdminEmail, UserName = devAdminEmail, DisplayName = "Admin" };
        await userService.CreateAsync(devAdmin, "Admin@123456");
    }
    else
    {
        // 已存在 → 解锁账号 + 重置密码（防止之前的登录失败锁死）
        await userService.ClearLockoutAsync(devAdmin);
        await userService.ResetPasswordAsync(devAdmin, "Admin@123456");
    }
    if (!await userService.IsInRoleAsync(devAdmin.Id, "admin"))
        await userService.AddToRoleAsync(devAdmin.Id, "admin");

    // —— 编辑精选合集：首次启动且尚无合集时，按曲库现状生成真实合集（幂等，不覆盖已有） ——
    var seededCollections = await ShineMusic.Service.EditorialCollectionSeeder.SeedIfEmptyAsync(sqlSugar);
    if (seededCollections > 0)
        app.Logger.LogInformation("已按曲库现状生成 {Count} 个编辑精选合集。", seededCollections);
}

// ===== 队列引导：恢复中断任务（重置中间态 + 重新投递 RabbitMQ）=====
// 必须在 CodeFirst 初始化之后执行：恢复逻辑读写业务状态机表。
await ShineMusic.Tasks.QueueBootstrap.InitializeAsync(app.Services);

// ===== 命令行模式：--create-admin 创建首位管理员 =====
// 这是系统的首个账号入口（注册采用邀请制）；仅在库中还没有任何用户时允许执行，
// 防止该参数被重复利用来越权追加管理员。流程结束后直接退出，不启动 Web 主机。
// 支持两种模式：
//   1) 环境变量非交互模式：设置 ADMIN_EMAIL / ADMIN_USERNAME / ADMIN_NAME / ADMIN_PASSWORD
//   2) 交互模式：未设置环境变量时，控制台提示输入
if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var sqlSugar = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
    var userService = scope.ServiceProvider.GetRequiredService<UserService>();
    if (await userService.AnyUsersAsync())
    {
        Console.Error.WriteLine("Admin initialization is only allowed before the first user exists.");
        Environment.ExitCode = 1;
        return;
    }

    // 优先从环境变量读取（非交互/自动化场景），缺失则回退到控制台交互输入。
    var email = Environment.GetEnvironmentVariable("ADMIN_EMAIL");
    var userName = Environment.GetEnvironmentVariable("ADMIN_USERNAME");
    var displayName = Environment.GetEnvironmentVariable("ADMIN_NAME");
    var password = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");

    if (string.IsNullOrWhiteSpace(email))
    {
        Console.Write("Admin email: ");
        email = Console.ReadLine()?.Trim();
    }
    if (string.IsNullOrWhiteSpace(displayName))
    {
        Console.Write("Display name: ");
        displayName = Console.ReadLine()?.Trim();
    }
    if (string.IsNullOrWhiteSpace(password))
    {
        Console.Write("Password: ");
        password = ReadPassword();
        Console.WriteLine();
    }
    if (string.IsNullOrWhiteSpace(userName))
    {
        userName = email; // 默认用邮箱作为登录用户名
    }

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine("Email, display name and password are required.");
        Environment.ExitCode = 1;
        return;
    }

    var admin = new ApplicationUser
    {
        Email = email,
        UserName = userName,
        DisplayName = displayName,
        EmailConfirmed = true
    };
    // 建用户与授予 admin 角色放在同一事务内，任一步失败都整体回滚，避免留下无角色的残留账号。
    await sqlSugar.Ado.BeginTranAsync();
    try
    {
        var createResult = await userService.CreateAsync(admin, password);
        if (!createResult.Succeeded)
        {
            await sqlSugar.Ado.RollbackTranAsync();
            Console.Error.WriteLine(string.Join(Environment.NewLine, createResult.Errors.Select(error => error.Description)));
            Environment.ExitCode = 1;
            return;
        }

        var roleResult = await userService.AddToRoleAsync(admin.Id, "admin");
        if (!roleResult.Succeeded)
        {
            await sqlSugar.Ado.RollbackTranAsync();
            Console.Error.WriteLine(string.Join(Environment.NewLine, roleResult.Errors.Select(error => error.Description)));
            Environment.ExitCode = 1;
            return;
        }

        await sqlSugar.Ado.CommitTranAsync();
    }
    catch
    {
        await sqlSugar.Ado.RollbackTranAsync();
        throw;
    }
    Console.WriteLine($"Initial administrator created: {admin.UserName} ({admin.Email}).");
    return;
}

app.Run();

// 控制台无回显读取密码（拦截按键、支持退格），避免输入的管理员密码显示在屏幕上或被滚屏记录/截屏。
static string ReadPassword()
{
    var password = new StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            password.Length--;
        else if (!char.IsControl(key.KeyChar))
            password.Append(key.KeyChar);
    }
    return password.ToString();
}

// 兜底错误体的状态码 → 错误码映射。与 Contracts/ErrorCodes 保持一致，
// 只覆盖「没有响应体」的一类状态，其余（400/401/409 等）均已由业务代码写出自定义错误体。
static string ResolveStatusCodeCode(int statusCode) => statusCode switch
{
    400 => ErrorCodes.ValidationError,
    401 => ErrorCodes.AuthenticationRequired,
    403 => ErrorCodes.PermissionDenied,
    404 => ErrorCodes.ResourceNotFound,
    405 => ErrorCodes.ResourceNotFound,
    406 => ErrorCodes.UnsupportedMediaFormat,
    408 => ErrorCodes.InternalError,
    413 => ErrorCodes.PayloadTooLarge,
    415 => ErrorCodes.UnsupportedMediaFormat,
    429 => ErrorCodes.RateLimited,
    >= 500 => ErrorCodes.InternalError,
    _ => ErrorCodes.ResourceNotFound
};
