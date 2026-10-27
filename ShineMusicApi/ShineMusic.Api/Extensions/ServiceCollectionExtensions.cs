// Program.cs 的服务注册扩展：把 Swagger、选项校验、CORS、基础设施（PG/Redis/RabbitMQ）、
// 应用服务、JWT 认证等大块配置收敛为 IServiceCollection 扩展方法，使 Program.cs 只保留调用入口。
// 每个扩展方法对应一块职责，新增配置时就近追加到对应方法即可，避免 Program.cs 持续膨胀。

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using ShineMusic.Model.Options;
using ShineMusic.Repository.Data;
using ShineMusic.Service;
using ShineMusic.Service.Lyrics;
using ShineMusic.Service.Messaging;
using ShineMusic.Service.PlaylistImport;
using ShineMusic.Service.SourceDownload;
using SqlSugar;

namespace ShineMusic.Api.Extensions;

/// <summary>IServiceCollection 服务注册扩展：按职责分块，调用方只需一行。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 Swagger/OpenAPI 文档（含 JWT Bearer 鉴权按钮）。</summary>
    public static IServiceCollection AddShineMusicSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "ShineMusic API",
                Version = "v1",
                Description = """
                    ShineMusic 音乐服务接口文档。

                    ## 鉴权
                    除标注为匿名的端点外，其余接口需在请求头携带 `Authorization: Bearer &lt;access-token&gt;`。
                    访问令牌通过 `/api/v1/auth/login` 获取，有效期 15 分钟；过期后用刷新令牌调用 `/api/v1/auth/refresh` 换取新令牌。

                    ## 错误响应
                    所有错误统一返回 `ApiErrorResponse`：
                    - `400` 参数校验失败
                    - `401` 未登录或令牌失效
                    - `403` 权限不足
                    - `404` 资源不存在
                    - `409` 冲突（如重复资源）
                    - `429` 触发限流
                    - `500` 服务端异常（响应体含 TraceId，可用于日志排查）
                    """,
                Contact = new OpenApiContact { Name = "ShineMusic Team" }
            });

            // 公网入口：Tailscale Funnel 地址（来自 config/hosts.json 的 server.webOrigin），
            // 让 Swagger UI 的 "Servers" 下拉直接给出可复制的对外访问地址。
            var funnelOrigin = Environment.GetEnvironmentVariable("SM_SWAGGER_SERVER");
            if (!string.IsNullOrWhiteSpace(funnelOrigin))
            {
                c.AddServer(new OpenApiServer { Url = funnelOrigin, Description = "公网入口（Tailscale Funnel）" });
            }
            c.AddServer(new OpenApiServer { Url = "/", Description = "当前主机（同源）" });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "输入 JWT（含 Bearer 前缀），例如：Bearer <your-access-token>"
            });
            // Microsoft.OpenApi 2.x 的 OpenApiSecurityRequirement 键类型改为 OpenApiSecuritySchemeReference（无公开无参构造），
            // 全局安全需求无法用旧写法拼装；改为按操作注入：只有带 [Authorize] 的端点声明 Bearer 要求。
            c.OperationFilter<ShineMusic.Api.OpenApi.BearerSecurityOperationFilter>();

            // XML 注释：Directory.Build.props 已开启 GenerateDocumentationFile，
            // 把 Api（Controllers）与 Model（DTO/枚举）的注释注入 Swagger，
            // 接口说明、参数说明、返回值说明与枚举成员描述都会出现在文档中。
            var xmlBase = AppContext.BaseDirectory;
            foreach (var xml in new[] { "ShineMusic.Api.xml", "ShineMusic.Model.xml", "ShineMusic.Service.xml" })
            {
                var xmlPath = Path.Combine(xmlBase, xml);
                if (File.Exists(xmlPath))
                {
                    // includeControllerXmlComments: true 让 Controller 类上的 <summary> 也作为 Tag 描述展示。
                    c.IncludeXmlComments(xmlPath, includeControllerXmlComments: xml.EndsWith("Api.xml", StringComparison.OrdinalIgnoreCase));
                }
            }

            // 让派生/可空类型的 schema 用 allOf 引用基类，展示更准确。
            c.UseAllOfToExtendReferenceSchemas();

            // 操作排序：先按 Tag 分组，再按 HTTP 方法（GET/POST/PUT/DELETE），最后按路径。
            c.OrderActionsBy(apiDesc =>
                $"{apiDesc.GroupName}_{apiDesc.HttpMethod}_{apiDesc.RelativePath}");
        });
        return services;
    }

    /// <summary>注册横切关注点：HttpClient、反向代理头、响应压缩、限流。</summary>
    public static IServiceCollection AddShineMusicCrossCutting(this IServiceCollection services)
    {
        services.AddHttpClient();
        // 反向代理头：让 HttpContext.Connection.RemoteIpAddress / Request.Scheme 反映真实客户端地址与协议。
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 2;
        });
        // 响应压缩：对 JSON 等文本响应启用 gzip/brotli。
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes
                .Concat(new[] { "application/json", "text/json" });
        });
        // 限流：对行为上报等写接口按用户（匿名按 IP）做固定窗口限流。
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("play-events", httpContext =>
                System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                        ?? ClientInfo.ResolveIp(httpContext),
                    factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));
        });
        return services;
    }

    /// <summary>注册 MVC 控制器 + 统一 400 错误体（把模型校验错误折叠为 ApiErrorResponse 结构）。</summary>
    public static IServiceCollection AddShineMusicControllers(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                // 枚举序列化为字符串（而非数字），Swagger 文档与实际 API 响应一致，前端联调更直观。
                options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            });
        services.Configure<ApiBehaviorOptions>(options =>
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
        return services;
    }

    /// <summary>注册全部强类型选项并绑定配置节、启动校验（ValidateOnStart）。</summary>
    public static IServiceCollection AddShineMusicOptions(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        services
            .AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), "Database:ConnectionString is required.")
            .ValidateOnStart();
        services
            .AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.LibraryRoot), "Storage:LibraryRoot is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.QuarantineRoot), "Storage:QuarantineRoot is required.")
            .ValidateOnStart();
        services
            .AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32, "Auth:SigningKey must be configured with at least 32 UTF-8 bytes.")
            .Validate(options => options.AccessTokenMinutes is >= 1 and <= 60, "Auth:AccessTokenMinutes must be between 1 and 60.")
            .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "Auth:RefreshTokenDays must be between 1 and 90.")
            .Validate(options => options.InvitationExpiryHours is >= 1 and <= 720, "Auth:InvitationExpiryHours must be between 1 and 720.")
            .ValidateOnStart();
        services.Configure<InvitationEmailOptions>(configuration.GetSection(InvitationEmailOptions.SectionName));
        services
            .AddOptions<RabbitOptions>()
            .BindConfiguration(RabbitOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.HostName), "RabbitMQ:HostName is required.")
            .ValidateOnStart();
        // 密码强度门槛（仅用于用户主动「修改密码」环节，不作用于 --create-admin 首位管理员创建）。
        services
            .AddOptions<PasswordPolicyOptions>()
            .BindConfiguration(PasswordPolicyOptions.SectionName)
            .Validate(options => options.MinimumStrength is >= 0 and <= 4, "PasswordPolicy:MinimumStrength must be between 0 and 4.")
            .ValidateOnStart();
        // 邀请邮件里的注册链接：appsettings 未显式配置时，由 config/hosts.json 按环境推导。
        services.PostConfigure<InvitationEmailOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return;
            var lanIp = configuration["local:lanIp"];
            var webPort = configuration["local:webPort"] ?? "5173";
            options.PublicBaseUrl = environment.IsDevelopment()
                ? (string.IsNullOrWhiteSpace(lanIp) ? $"http://localhost:{webPort}" : $"http://{lanIp}:{webPort}")
                : configuration["server:webOrigin"] ?? string.Empty;
        });
        services
            .AddOptions<FeatureOptions>()
            .BindConfiguration(FeatureOptions.SectionName)
            .Validate(options => options.ManualReview, "Features:ManualReview must be enabled for the first release.")
            .Validate(options => options.ServerTranscoding, "Features:ServerTranscoding must be enabled for the first release.")
            .ValidateOnStart();
        services
            .AddOptions<SongRequestFulfillmentOptions>()
            .BindConfiguration(SongRequestFulfillmentOptions.SectionName)
            .Validate(options => options.Checks.ContainsKey("TitleArtistMatch"), "SongRequestFulfillment:Checks:TitleArtistMatch is required.")
            .Validate(options => options.TitleArtistMatch.MaxEditDistance is >= 0 and <= 5, "SongRequestFulfillment:TitleArtistMatch:MaxEditDistance must be between 0 and 5.")
            .ValidateOnStart();
        services
            .AddOptions<SongRequestOptions>()
            .BindConfiguration(SongRequestOptions.SectionName)
            .Validate(options => options.ArchiveAfterDays is >= 1 and <= 3650, "SongRequests:ArchiveAfterDays must be between 1 and 3650.")
            .Validate(options => options.ArchiveScanIntervalMinutes is >= 1 and <= 1440, "SongRequests:ArchiveScanIntervalMinutes must be between 1 and 1440.")
            .ValidateOnStart();
        services
            .AddOptions<UploadOptions>()
            .BindConfiguration(UploadOptions.SectionName)
            .Validate(options => options.MaxFileSizeBytes is > 0 and <= 1073741824, "Uploads:MaxFileSizeBytes must be between 1 byte and 1 GiB.")
            .Validate(options => options.MaxBatchSizeBytes >= options.MaxFileSizeBytes, "Uploads:MaxBatchSizeBytes must be at least MaxFileSizeBytes.")
            .Validate(options => options.MaxBatchSizeBytes <= 4294967296, "Uploads:MaxBatchSizeBytes must not exceed 4 GiB.")
            .Validate(options => options.MaxFilesPerBatch is >= 1 and <= 20, "Uploads:MaxFilesPerBatch must be between 1 and 20.")
            .Validate(options => options.AllowedFormats.Count > 0, "Uploads:AllowedFormats must contain at least one format.")
            .ValidateOnStart();
        // multipart 表单解析层体积上限与 Kestrel 上限对齐。
        services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 4294967296);
        services
            .AddOptions<TranscodingOptions>()
            .BindConfiguration(TranscodingOptions.SectionName)
            .Validate(options => options.TargetFormat == "aac_m4a", "Only the configured aac_m4a output is currently supported.")
            .Validate(options => options.BitrateKbps is >= 96 and <= 320, "Transcoding:BitrateKbps must be between 96 and 320.")
            .Validate(options => options.MaxConcurrentJobs is >= 1 and <= 4, "Transcoding:MaxConcurrentJobs must be between 1 and 4.")
            .ValidateOnStart();
        services.Configure<FfmpegOptions>(configuration.GetSection(FfmpegOptions.SectionName));
        services
            .AddOptions<LyricsOptions>()
            .BindConfiguration(LyricsOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Lyrics:BaseUrl is required.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 30, "Lyrics:TimeoutSeconds must be between 1 and 30.")
            .Validate(options => options.MinScore is >= 0 and <= 1, "Lyrics:MinScore must be between 0 and 1.")
            .ValidateOnStart();
        services
            .AddOptions<LibraryImportOptions>()
            .BindConfiguration(LibraryImportOptions.SectionName)
            .Validate(options => options.AutoPublishThreshold is >= 0 and <= 100,
                "LibraryImport:AutoPublishThreshold must be between 0 and 100.")
            .ValidateOnStart();
        services
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
        return services;
    }

    /// <summary>注册 CORS 策略：优先 appsettings 显式配置，未配置时由 config/hosts.json 推导。</summary>
    public static IServiceCollection AddShineMusicCors(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        if (allowedOrigins.Length == 0)
        {
            var lanIp = configuration["local:lanIp"];
            var webPort = configuration["local:webPort"] ?? "5173";
            var serverWebOrigin = configuration["server:webOrigin"];
            allowedOrigins = environment.IsDevelopment()
                ? (string.IsNullOrWhiteSpace(lanIp)
                    ? [$"http://localhost:{webPort}", $"http://127.0.0.1:{webPort}"]
                    : [$"http://localhost:{webPort}", $"http://127.0.0.1:{webPort}", $"http://{lanIp}:{webPort}"])
                // "null"：5+App/HBuilderX 本地壳的页面来源是 file://，浏览器规范规定其跨域请求 Origin 头为字面量 "null"
                : (string.IsNullOrWhiteSpace(serverWebOrigin) ? [] : [serverWebOrigin, "null"]);
        }
        services.AddCors(options => options.AddPolicy("Client", policy =>
        {
            // 未配置任何来源时不放行任何跨域请求（失败即关闭），避免开发期宽松配置被误带到生产环境。
            if (allowedOrigins.Length > 0)
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }));
        return services;
    }

    /// <summary>注册基础设施：PostgreSQL（SqlSugar 作用域）、Redis 缓存（无连接串时回退内存）、RabbitMQ 消息总线与消费者。</summary>
    public static IServiceCollection AddShineMusicInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ===== 数据库：PostgreSQL（SqlSugar）=====
        var configuredConnectionString = configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];
        // 作用域注册：每个 HTTP 请求、每条队列消息、每轮定时任务各持一个 SqlSugarClient。
        services.AddScoped<SqlSugarClient>(serviceProvider =>
        {
            _ = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            return SqlSugarFactory.Create(configuredConnectionString!, serviceProvider.GetRequiredService<ILoggerFactory>());
        });
        services.AddScoped<ISqlSugarClient>(serviceProvider => serviceProvider.GetRequiredService<SqlSugarClient>());

        // ===== Redis：分布式缓存（无连接串时回退进程内内存缓存）=====
        var redisConnectionString = configuration[$"{RedisOptions.SectionName}:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
                StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnectionString;
                options.InstanceName = configuration[$"{RedisOptions.SectionName}:InstanceName"] ?? "shinemusic:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        // ===== RabbitMQ：持久化消息队列 =====
        services.AddSingleton<RabbitMessageBus>();
        services.AddSingleton<IRabbitMessageBus>(sp => sp.GetRequiredService<RabbitMessageBus>());
        services.AddHostedService<ShineMusic.Tasks.SourceDownloadConsumerService>();
        services.AddHostedService<ShineMusic.Tasks.LibraryImportConsumerService>();
        // 周期任务定时器。
        services.AddHostedService<ShineMusic.Tasks.LyricsBackfillService>();
        services.AddHostedService<ShineMusic.Tasks.SongRequestArchiveService>();
        services.AddHostedService<ShineMusic.Tasks.LibraryImportRetrySweepService>();
        services.AddHostedService<ShineMusic.Tasks.SourceDownloadRetrySweepService>();
        // 业务处理器注册。
        services.AddTransient<LibraryImportJob>();
        services.AddTransient<LibraryImportRetrySweepJob>();
        services.AddTransient<SourceDownloadJob>();
        services.AddTransient<SourceDownloadRetrySweepJob>();
        services.AddTransient<ShineMusic.Tasks.LyricsBackfillJob>();
        services.AddTransient<ShineMusic.Tasks.SongRequestArchiveJob>();
        return services;
    }

    /// <summary>注册应用服务：存储、转码、歌词、音源抓歌、歌单导入、用户/鉴权等。</summary>
    public static IServiceCollection AddShineMusicApplicationServices(this IServiceCollection services)
    {
        services.AddHttpClient();
        // 存储路径定位、转码/元数据服务。
        services.AddSingleton<LibraryStoragePaths>();
        services.AddSingleton<QuarantineStoragePaths>();
        services.AddSingleton<FfmpegMediaService>();
        services.AddSingleton<AudioMetadataParser>();
        services.AddSingleton<ArtworkFetcher>();
        services.AddScoped<ImportStagingService>();
        services.AddScoped<ImportPublishService>();
        // 歌词解析器与外部源提供者链。
        services.AddHttpClient(LyricsResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<IExternalLyricsProvider, LrclibLyricsProvider>();
        services.AddSingleton<IExternalLyricsProvider, KuGouLyricsProvider>();
        services.AddSingleton<IExternalLyricsProvider, MiguLyricsProvider>();
        services.AddSingleton<ILyricsResolver, LyricsResolver>();
        services.AddScoped<StorageGovernanceService>();
        services.AddScoped<RecommendationService>();

        // ===== 外部音源抓歌：命名 HttpClient + provider 集合 =====
        services.AddHttpClient(AudiusSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(JamendoSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(YouTubeMusicSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(SoundCloudSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddHttpClient(KuwoSourceProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(120));
        // 咪咕：显式用 SocketsHttpHandler 限定 TLS1.2/1.3 并关闭 Expect: 100-continue。
        services.AddHttpClient(MiguSourceProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.ExpectContinue = false;
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
        services.AddSingleton<IMusicSourceProvider, SoundCloudSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, KuwoSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, MiguSourceProvider>();
        services.AddSingleton<IMusicSourceProvider, NeteaseSourceProvider>();
        services.AddScoped<SourceDownloadService>();

        // ===== 外部歌单解析导入 =====
        services.AddHttpClient(NeteasePlaylistParser.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(120);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        });
        services.AddHttpClient(QQMusicPlaylistParser.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
            client.DefaultRequestHeaders.Referrer = new Uri("https://y.qq.com/");
        });
        services.AddSingleton<IPlaylistParser, NeteasePlaylistParser>();
        services.AddSingleton<IPlaylistParser, QQMusicPlaylistParser>();
        services.AddSingleton<IPlaylistParser, KuwoPlaylistParser>();
        services.AddSingleton<IPlaylistParser, YtDlpPlaylistParser>();
        services.AddScoped<PlaylistImportService>();

        // ===== 用户/鉴权服务 =====
        services.AddScoped<UserService>();
        services.AddSingleton<TokenService>();
        services.AddTransient<IInvitationEmailSender, InvitationEmailSender>();
        return services;
    }

    /// <summary>注册 JWT 认证（含 401/403 统一 JSON 错误响应与 query-string 令牌透传）。</summary>
    public static IServiceCollection AddShineMusicAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authConfiguration = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        // 启动时显式检查签名密钥：密钥缺失或不足 32 字节时直接拒绝启动。
        if (Encoding.UTF8.GetByteCount(authConfiguration.SigningKey) < 32)
            throw new InvalidOperationException("Set Auth__SigningKey in User Secrets or the server environment before starting the API.");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authConfiguration.SigningKey));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
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
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = ClaimTypes.Role
                };
                options.Events = new JwtBearerEvents
                {
                    // <audio>/<video> 元素无法携带 Authorization 头，对试听/预览类接口允许通过 query string 传递 access_token。
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
                    // 未携带/无效令牌：统一返回 ApiErrorResponse JSON。
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                            new ApiErrorDetail(ErrorCodes.AuthenticationRequired, "Authentication is required.", context.HttpContext.TraceIdentifier)));
                    },
                    // 权限不足：统一返回 403 JSON。
                    OnForbidden = async context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                            new ApiErrorDetail(ErrorCodes.PermissionDenied, "You do not have permission to access this resource.", context.HttpContext.TraceIdentifier)));
                    }
                };
            });
        services.AddAuthorization();
        return services;
    }
}
