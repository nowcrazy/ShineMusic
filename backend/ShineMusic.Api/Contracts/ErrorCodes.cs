// =============================================================================
// 文件：Contracts/ErrorCodes.cs
// 用途：全站错误码唯一来源。前后端均以此为准，禁止在业务代码里写字面量。
//
// 背景：审计发现错误码以裸字符串形式散落在各处——
//   后端 Endpoints 下 resource_not_found 出现 18 次、validation_error 14 次、resource_conflict 13 次；
//   前端在 6 个文件中以 error.code === 'xxx' 直接比较。
// 字符串拼错不会报错，只会让分支静默失效；后端新增错误码前端也无从得知。
// 收拢到常量类后，重命名可以全局编译检查，OpenAPI 也能据此生成 code 字段的枚举约束。
//
// 命名约定：snake_case，语义稳定，**不随文案改动**。客户端应依赖 code 而非 message。
// =============================================================================
namespace ShineMusic.Api.Contracts;

/// <summary>
/// 全站 API 错误码常量。与 <see cref="ApiErrorResponse"/> 配套使用。
/// 前端 services/errors.ts 中有一份同构副本，由生成器/人工保持同步。
/// </summary>
public static class ErrorCodes
{
    // ===== 通用 =====
    /// <summary>请求参数缺失、类型错误或超出取值范围（包含 JSON 反序列化失败）。</summary>
    public const string ValidationError = "validation_error";
    /// <summary>目标资源不存在，或按当前用户权限不可见（统一用 404 避免暴露存在性）。</summary>
    public const string ResourceNotFound = "resource_not_found";
    /// <summary>与资源当前状态冲突（重复提交、状态机不允许的迁移、唯一键冲突等）。</summary>
    public const string ResourceConflict = "resource_conflict";
    /// <summary>未携带有效凭据或凭据已失效。</summary>
    public const string AuthenticationRequired = "authentication_required";
    /// <summary>已登录但角色或归属校验不通过（用户越权访问他人资源）。</summary>
    public const string PermissionDenied = "permission_denied";
    /// <summary>请求过于频繁，被限流中间件拒绝。</summary>
    public const string RateLimited = "rate_limited";
    /// <summary>HTTP 方法不被该路由支持，或资源已产出但内容类型不可协商。</summary>
    public const string UnsupportedMediaFormat = "unsupported_media_format";
    /// <summary>未预期的服务端异常。消息面向用户脱敏，细节只在服务端日志。</summary>
    public const string InternalError = "internal_error";
    /// <summary>下游 I/O（存储、转码、邮件）环节出错。</summary>
    public const string StorageError = "storage_error";
    /// <summary>请求体超过客户端/服务端限制（如上传体积超限）。</summary>
    public const string PayloadTooLarge = "payload_too_large";

    // ===== 邮箱/密码（注册登录） =====
    /// <summary>邮箱或密码不正确。刻意不区分「用户不存在」与「密码错误」，避免账号枚举。</summary>
    public const string EmailOrPasswordInvalid = "email_or_password_invalid";
    /// <summary>重置密码令牌无效或已被使用。</summary>
    public const string PasswordResetInvalid = "password_reset_invalid";
    /// <summary>注册时需要邀请令牌。</summary>
    public const string InvitationRequired = "invitation_required";

    // ===== 邀请（Invitations） =====
    /// <summary>邀请令牌不存在、格式错误，或与请求邮箱不匹配。</summary>
    public const string InvitationInvalid = "invitation_invalid";
    /// <summary>邀请已过期。</summary>
    public const string InvitationExpired = "invitation_expired";
    /// <summary>邀请已被兑换（一次性令牌）。</summary>
    public const string InvitationRedeemed = "invitation_redeemed";

    // ===== 用户上传 / 曲库（User Uploads / Library） =====
    /// <summary>上传文件超过单文件体积上限（上限见系统能力快照 maxUploadSizeBytes）。</summary>
    public const string UploadTooLarge = "upload_too_large";
    /// <summary>音频容器/标签无法解析，文件可能损坏或不被 ffprobe 支持。</summary>
    /// <remarks>
    /// 值保持为 <c>media_parse_failed</c>（历史契约）。这是抓取时发现的既有代码，
    /// 不要顺手改成更"贴切"的 metadata_parse_failed——线上客户端可能已经在判断它。
    /// </remarks>
    public const string MediaParseFailed = "media_parse_failed";
    /// <summary>无法引用他人私有曲目（内容哈希命中但归属他人）。</summary>
    public const string PrivateTrackOfAnotherUser = "private_track_of_another_user";
    /// <summary>目标曲目为他人私有，当前用户无权引用或试听。</summary>
    public const string TrackNotAccessible = "track_not_accessible";

    // ===== 媒体处理（转码 / 歌词） =====
    /// <summary>转码环节失败（ffmpeg 退出码非零或产物缺失）。</summary>
    public const string TranscodeFailed = "transcode_failed";
    /// <summary>外部歌词源查询失败或返回不可用内容。</summary>
    public const string LyricsSourceFailed = "lyrics_source_failed";

    // ===== 求歌（Song Requests）与导入校验 =====
    /// <summary>曲目与目标需求不一致，需要管理员确认后才可发布。</summary>
    public const string SongRequestMismatch = "song_request_mismatch";
    /// <summary>不一致且策略配置为强制阻断，必须修正元数据或取消关联需求后才能发布。</summary>
    public const string SongRequestMismatchOverrideRequired = "song_request_mismatch_override_required";

    // ===== 头像（Avatar） =====
    /// <summary>头像文件体积超限。</summary>
    public const string AvatarTooLarge = "avatar_too_large";

    // ===== 邮件（Invitation Email） =====
    /// <summary>邮件投递失败（SMTP 连接或鉴权问题）。详情见服务端日志，不对客户端暴露。</summary>
    public const string EmailDeliveryFailed = "email_delivery_failed";

    // ===== 邀请码 / 推荐 / 反馈（预留） =====
    /// <summary>请求体字段格式正确但语义不合法（如自引用、越界索引）。</summary>
    public const string SemanticInvalid = "semantic_invalid";
}
