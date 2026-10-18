// 应用程序入口：组装依赖注入容器（选项校验、SQLite 数据库、Identity、JWT、CORS 与应用服务）、
// 配置 HTTP 中间件管道与端点映射；启动时自动执行数据库迁移并播种角色，
// 另支持以 --create-admin 命令行参数交互式创建首位管理员（创建后直接退出，不启动 Web 服务）。
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using ShineMusic.Api.Data;
using ShineMusic.Api.Endpoints;
using ShineMusic.Api.Logging;
using ShineMusic.Api.Options;
using ShineMusic.Api.Services;
using ShineMusic.Api.Contracts;
using Microsoft.OpenApi;

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

// ===== 文件日志：操作日志(operations)与数据执行日志(data)按日期分离落盘 =====
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
    // OpenApiSecuritySchemeReference（无公开无参构造），手工拼装全局安全需求较繁琐；
    // 这里保留 Bearer 安全定义供 Swagger UI 识别，如需“Authorize”按钮全局生效可后续加
    // IDocumentFilter 适配。当前端到端联调仍以真实 JWT 为主。
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
        : (string.IsNullOrWhiteSpace(serverWebOrigin) ? [] : [serverWebOrigin]);
}
builder.Services.AddCors(options => options.AddPolicy("Client", policy =>
{
    // 未配置任何来源时不放行任何跨域请求（失败即关闭），避免开发期宽松配置被误带到生产环境。
    if (allowedOrigins.Length > 0)
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

// ===== 数据库：SQLite 相对路径归一化与 DbContext 注册 =====
var configuredConnectionString = builder.Configuration[$"{DatabaseOptions.SectionName}:ConnectionString"];
var sqliteConnection = new SqliteConnectionStringBuilder(configuredConnectionString);
// 相对路径若原样交给 SQLite，数据库文件位置会随进程的当前工作目录变化，可能在不同启动方式下连到不同的库；
// 因此统一按应用内容根目录解析为绝对路径（":memory:" 内存库与已是绝对路径的场景保持原样）。
if (!string.IsNullOrWhiteSpace(sqliteConnection.DataSource) &&
    sqliteConnection.DataSource != ":memory:" &&
    !Path.IsPathRooted(sqliteConnection.DataSource))
{
    sqliteConnection.DataSource = Path.GetFullPath(sqliteConnection.DataSource, builder.Environment.ContentRootPath);
    // 首次启动时数据库文件尚不存在，需先确保其父目录存在，否则迁移建库会因目录缺失而失败。
    Directory.CreateDirectory(Path.GetDirectoryName(sqliteConnection.DataSource)!);
}

builder.Services.AddDbContext<MusicDbContext>((serviceProvider, options) =>
{
    // 显式解析一次选项对象以触发 DatabaseOptions 的启动校验，确保连接字符串在上下文首次使用前已通过验证。
    _ = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        options.UseSqlite(
            sqliteConnection.ToString(),
            // 含艺术家/曲目等集合导航的查询拆成多条 SQL 执行，避免 EF Core 单查询 JOIN 造成笛卡尔积数据膨胀。
            sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        // 数据执行日志拦截器：把每条实际执行的 SQL 写入 logs/data（按日期滚动）。
        options.AddInterceptors(new DataExecutionInterceptor(serviceProvider.GetRequiredService<ILoggerFactory>()));
});
// ===== 应用服务：存储路径定位、导入队列、转码/元数据服务与后台导入服务的生命周期注册 =====
builder.Services.AddSingleton<LibraryStoragePaths>();
builder.Services.AddSingleton<QuarantineStoragePaths>();
builder.Services.AddSingleton<LibraryImportQueue>();
builder.Services.AddSingleton<FfmpegMediaService>();
builder.Services.AddSingleton<AudioMetadataParser>();
builder.Services.AddSingleton<ArtworkFetcher>();
builder.Services.AddScoped<ImportStagingService>();
// 歌词解析器：无状态，注册为单例；命名 HttpClient 单独控制外部歌词源的连接与超时。
builder.Services.AddHttpClient(LyricsResolver.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ILyricsResolver, LyricsResolver>();
// 存储治理服务：依赖 MusicDbContext，按请求作用域注册。
builder.Services.AddScoped<StorageGovernanceService>();
// 每日推荐服务：依赖 MusicDbContext，按请求作用域注册。
builder.Services.AddScoped<RecommendationService>();
// 后台服务常驻消费导入队列，按 Transcoding:MaxConcurrentJobs 控制 ffmpeg 并发。
builder.Services.AddHostedService<LibraryImportBackgroundService>();
// 点歌需求自动归档：定时把已满足且超期的需求从公共池隐藏（记录保留）。
builder.Services.AddHostedService<SongRequestArchiveService>();
// ===== Identity：用户/角色存储、密码强度与登录锁定策略（基于 EF Core 持久化） =====
builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // 开发环境密码策略：至少 6 位，不强制大小写/数字/特殊符号，便于本地测试。
        // 生产环境应收紧为 12 位+数字+大小写。
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 6;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        // 连续 5 次密码错误锁定 15 分钟，抵御针对账号密码的暴力破解。
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<MusicDbContext>()
    .AddSignInManager();
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

// OpenAPI/Swagger 文档与可交互 UI 仅在开发环境暴露，生产环境不公开接口结构。
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ShineMusic API v1");
        c.RoutePrefix = "swagger"; // 访问根路径 /swagger 即可打开 UI
    });
}

// ===== 中间件管道（顺序敏感）：异常处理 → CORS → 认证 → 授权 → 端点路由 =====
// 未处理异常统一收敛为 500 JSON，先记录带 TraceId 的日志，再向客户端返回脱敏错误信息。
app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    // 未处理异常统一收敛为 500 JSON，先记录带 TraceId 的日志（落入 logs/app），再向客户端返回脱敏错误信息。
    // 日志带方法/路径/TraceId，配合 operations/data 日志即可顺着 TraceId 还原整条请求链路。
    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("App.UnhandledException")
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
app.UseMiddleware<ShineMusic.Api.Middleware.OperationLoggingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// 限流中间件：需在端点路由之前，[EnableRateLimiting] 标注的端点才会生效。
app.UseRateLimiter();

// ===== 端点映射：注册控制器路由（必须在认证/授权之后，否则鉴权中间件不生效） =====
app.MapControllers();

// ===== 启动数据准备：自动应用 EF Core 迁移建库/升级表结构，并确保 user、admin 两个内置角色已播种 =====
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
    await db.Database.MigrateAsync();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
    foreach (var roleName in new[] { "user", "admin" })
    {
        if (!await roleManager.RoleExistsAsync(roleName))
            await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
    }

    // —— 开发环境确保管理员账号可用（存在则重置密码+加角色；不存在则创建） ——
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var devAdminEmail = "3449352732@qq.com";
    var devAdmin = await userManager.FindByEmailAsync(devAdminEmail);
    if (devAdmin is null)
    {
        devAdmin = new ApplicationUser { Email = devAdminEmail, UserName = devAdminEmail, DisplayName = "Admin" };
        await userManager.CreateAsync(devAdmin, "Admin@123456");
    }
    else
    {
        // 已存在 → 解锁账号 + 重置密码（防止之前的登录失败锁死）
        if (devAdmin.LockoutEnd.HasValue && devAdmin.LockoutEnd > DateTimeOffset.UtcNow)
            devAdmin.LockoutEnd = null;
        devAdmin.AccessFailedCount = 0;
        await userManager.RemovePasswordAsync(devAdmin);
        await userManager.AddPasswordAsync(devAdmin, "Admin@123456");
        await userManager.UpdateAsync(devAdmin);
    }
    if (!await userManager.IsInRoleAsync(devAdmin, "admin"))
        await userManager.AddToRoleAsync(devAdmin, "admin");

    // —— 编辑精选合集：首次启动且尚无合集时，按曲库现状生成真实合集（幂等，不覆盖已有） ——
    var seededCollections = await ShineMusic.Api.Services.EditorialCollectionSeeder.SeedIfEmptyAsync(db);
    if (seededCollections > 0)
        app.Logger.LogInformation("已按曲库现状生成 {Count} 个编辑精选合集。", seededCollections);
}

// ===== 命令行模式：--create-admin 创建首位管理员 =====
// 这是系统的首个账号入口（注册采用邀请制）；仅在库中还没有任何用户时允许执行，
// 防止该参数被重复利用来越权追加管理员。流程结束后直接退出，不启动 Web 主机。
// 支持两种模式：
//   1) 环境变量非交互模式：设置 ADMIN_EMAIL / ADMIN_USERNAME / ADMIN_NAME / ADMIN_PASSWORD
//   2) 交互模式：未设置环境变量时，控制台提示输入
if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MusicDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    if (await userManager.Users.AnyAsync())
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
    await using var transaction = await db.Database.BeginTransactionAsync();
    var createResult = await userManager.CreateAsync(admin, password);
    if (!createResult.Succeeded)
    {
        await transaction.RollbackAsync();
        Console.Error.WriteLine(string.Join(Environment.NewLine, createResult.Errors.Select(error => error.Description)));
        Environment.ExitCode = 1;
        return;
    }

    var roleResult = await userManager.AddToRoleAsync(admin, "admin");
    if (!roleResult.Succeeded)
    {
        await transaction.RollbackAsync();
        Console.Error.WriteLine(string.Join(Environment.NewLine, roleResult.Errors.Select(error => error.Description)));
        Environment.ExitCode = 1;
        return;
    }

    await transaction.CommitAsync();
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
