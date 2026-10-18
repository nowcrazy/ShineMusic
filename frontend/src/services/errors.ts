/**
 * 统一错误处理模块：错误码常量 + 错误文案映射。
 *
 * 存在的理由（来自 2026-10-18 的契约审计）：
 * - 审计前，错误码以字面量形式散落在 `login.vue`、`register.vue`、`requests/index.vue`、
 *   `admin/import.vue`、`profile/index.vue` 五个文件里，各写各的；
 * - 后端同样以裸字符串在 20 处重复。两边拼错一个字母，`===` 永远 false，分支静默失效且不报错。
 *
 * 本模块是**前端侧的错误码唯一来源**，与后端 `Contracts/ErrorCodes.cs` 一一对应。
 * 两个文件必须同步修改；后续若接入 OpenAPI 生成类型，此处可直接替换为生成的联合类型。
 *
 * 约定：
 * - 文案只认 `ErrorCodes`，不要在页面里再写 `error.code === 'xxx'`；
 * - 未命中映射的错误码统一回落到后端 message，保证新码不显示成空白；
 * - 网络层错误（`status === 0`）在这里统一为「无法连接服务器」。
 */

import { ApiError } from './http'

/**
 * 错误码常量，值必须与后端 `ShineMusic.Api.Contracts.ErrorCodes` 完全一致。
 * 以 `as const` 收窄为字面量联合类型，写错的键在编译期就会被发现。
 */
export const ErrorCodes = {
  // ===== 通用 =====
  ValidationError: 'validation_error',
  ResourceNotFound: 'resource_not_found',
  ResourceConflict: 'resource_conflict',
  AuthenticationRequired: 'authentication_required',
  PermissionDenied: 'permission_denied',
  RateLimited: 'rate_limited',
  UnsupportedMediaFormat: 'unsupported_media_format',
  InternalError: 'internal_error',
  StorageError: 'storage_error',
  PayloadTooLarge: 'payload_too_large',

  // ===== 邮箱/密码 =====
  EmailOrPasswordInvalid: 'email_or_password_invalid',
  PasswordResetInvalid: 'password_reset_invalid',
  InvitationRequired: 'invitation_required',

  // ===== 邀请 =====
  InvitationInvalid: 'invitation_invalid',
  InvitationExpired: 'invitation_expired',
  InvitationRedeemed: 'invitation_redeemed',

  // ===== 上传 / 曲库 =====
  UploadTooLarge: 'upload_too_large',
  MediaParseFailed: 'media_parse_failed',
  PrivateTrackOfAnotherUser: 'private_track_of_another_user',
  TrackNotAccessible: 'track_not_accessible',

  // ===== 媒体处理 =====
  TranscodeFailed: 'transcode_failed',
  LyricsSourceFailed: 'lyrics_source_failed',

  // ===== 求歌与导入校验 =====
  SongRequestMismatch: 'song_request_mismatch',
  SongRequestMismatchOverrideRequired: 'song_request_mismatch_override_required',

  // ===== 头像 =====
  AvatarTooLarge: 'avatar_too_large',

  // ===== 邮件 =====
  EmailDeliveryFailed: 'email_delivery_failed',
} as const

export type ErrorCode = (typeof ErrorCodes)[keyof typeof ErrorCodes]

/** 网络层错误标识：后端不可达、DNS 失败、超时。http.ts 在 uni.request fail 时抛出。 */
export const NETWORK_ERROR_CODE = 'network_error'

/** 后端未返回 code 时 http.ts 的兜底码。 */
export const FALLBACK_ERROR_CODE = 'internal_error'

/**
 * 错误码 → 中文文案。未覆盖的码走通用回落，不在此表中吐默认文案。
 */
const MESSAGES: Record<string, string> = {
  [ErrorCodes.AuthenticationRequired]: '邮箱或密码不正确',
  [ErrorCodes.EmailOrPasswordInvalid]: '邮箱或密码不正确',
  [ErrorCodes.InvitationInvalid]: '邀请令牌无效',
  [ErrorCodes.InvitationExpired]: '邀请链接已过期',
  [ErrorCodes.InvitationRedeemed]: '邀请链接已经被使用',
  [ErrorCodes.InvitationRequired]: '注册需要邀请链接，请联系管理员获取',
  [ErrorCodes.ResourceNotFound]: '内容不存在或已被删除',
  [ErrorCodes.ResourceConflict]: '操作冲突，请刷新后重试',
  [ErrorCodes.PermissionDenied]: '没有权限执行此操作',
  [ErrorCodes.RateLimited]: '操作太频繁了，请稍后再试',
  [ErrorCodes.ValidationError]: '填写内容有误，请检查后重试',
  [ErrorCodes.UploadTooLarge]: '文件超出大小上限',
  [ErrorCodes.AvatarTooLarge]: '头像不能超过 2MB',
  [ErrorCodes.UnsupportedMediaFormat]: '文件格式不受支持',
  [ErrorCodes.MediaParseFailed]: '文件无法解析，可能已损坏或格式不对',
  [ErrorCodes.TranscodeFailed]: '音频转码失败，请换一个文件重试',
  [ErrorCodes.LyricsSourceFailed]: '歌词匹配失败，可稍后手动补充',
  [ErrorCodes.PrivateTrackOfAnotherUser]: '这是其他用户的私有曲目，无法引用',
  [ErrorCodes.TrackNotAccessible]: '该曲目暂不可播放',
  [ErrorCodes.SongRequestMismatch]: '与需求信息不一致',
  [ErrorCodes.SongRequestMismatchOverrideRequired]: '与需求信息不一致，需修正元数据后才能发布',
  [ErrorCodes.StorageError]: '存储服务异常，请稍后再试',
  [ErrorCodes.EmailDeliveryFailed]: '邮件发送失败，请稍后重试或联系管理员',
  [ErrorCodes.InternalError]: '服务开小差了，请稍后再试',
}

/**
 * 把任意异常转换为可直接展示的中文文案。
 *
 * 优先级：错误码映射 → 后端 message（新错误码不会变空白）→ 网络/兜底文案。
 *
 * @param error 业务代码捕获到的异常（期望为 ApiError）
 * @param fallback 完全无法识别时展示的兜底文案，默认按场景给通用语
 * @returns 面向用户的中文提示
 */
export function describeError(error: unknown, fallback = '操作失败，请稍后再试'): string {
  if (!(error instanceof ApiError)) return fallback

  // 网络层：status 0 表示请求没到达后端，message 是 uni.app 的底层 errMsg，不适合展示
  if (error.status === 0 || error.code === NETWORK_ERROR_CODE) {
    return '无法连接服务器，请检查网络或 API 配置'
  }
  if (error.status === 429) return MESSAGES[ErrorCodes.RateLimited]

  // 带字段名的校验错误（message 形如 "title: Title is required..."）保留原文，便于定位表单项
  if (error.code === ErrorCodes.ValidationError) {
    return fieldNameToChinese(error.message)
  }

  return MESSAGES[error.code] ?? error.message ?? fallback
}

/** 后端校验错误的字段名 → 中文字段名，便于用户看懂 "title: ..." 这类提示。 */
const FIELD_LABELS: Record<string, string> = {
  title: '标题',
  artistName: '艺人',
  albumTitle: '专辑',
  email: '邮箱',
  password: '密码',
  displayName: '昵称',
  avatar: '头像',
  artworkId: '封面',
  genres: '流派',
  trackNumber: '曲序',
  discNumber: '碟号',
  releaseYear: '发行年份',
  page: '页码',
  pageSize: '每页条数',
  status: '状态',
  songRequestId: '关联需求',
  request: '请求体',
}

/**
 * 把 "title: Title is required." 改写成 "标题：Title is required."。
 * 仅替换开头第一个字段名，其余内容原样保留（后半句是后端英文提示，不做机器翻译）。
 */
function fieldNameToChinese(message: string): string {
  const match = /^([A-Za-z_][A-Za-z0-9_]*):\s*/.exec(message)
  if (!match) return message
  const label = FIELD_LABELS[match[1]]
  return label ? `${label}：${message.slice(match[0].length)}` : message
}

/**
 * 判断某类错误是否需要引导用户重新登录（例如凭据失效）。
 * 目前后端未区分「未登录」与「登录态过期」，统一由 http.ts 的 401 刷新流程处理，
 * 这里留出扩展点，避免以后在页面里散写 `error.status === 401`。
 */
export function shouldReLogin(error: unknown): boolean {
  return error instanceof ApiError && error.code === ErrorCodes.AuthenticationRequired
}
