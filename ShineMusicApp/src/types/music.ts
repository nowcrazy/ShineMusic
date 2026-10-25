/**
 * 前端类型入口：后端契约的唯一使用面。
 *
 * 2026-10-18 契约审计后，这里从「手写 34 个接口镜像后端 DTO」改为
 * 「转发自动生成的契约类型」。改动要点：
 *
 * - `./api.generated.ts` 由 `ShineMusicApi/tools/generate-ts-contracts.py` 从后端
 *   `public sealed record` 生成，**禁止手工修改**；后端改契约后重新生成即可。
 * - 本文件负责三件事，且只做这三件事：
 *     1. 把生成类型以「历史沿用名」re-export，业务代码 import 路径不变；
 *     2. 定义少量没有对应后端 DTO 的纯前端类型（查询参数对象、UI 视图模型）；
 *     3. 定义联合类型（如 SongRequestStatus）——后端以 string 承载的封闭集合。
 *
 * 因此：**不要在 services/ 或 pages/ 里新增契约接口**。新增一律先在后端 Contracts/ 加 record，
 * 再跑 `yarn gen:contracts`，最后在这里补一行别名。
 */
import type {
  ApiArtistSummary,
  ApiInvitationValidation,
  ApiPrecheckMatch,
  ApiSongRequest as GeneratedApiSongRequest,
  ApiLyric as GeneratedApiLyric,
  ApiAlbumSummary,
  ApiAudio,
  ApiAuthToken,
  ApiCreateComment,
  ApiCurrentUser,
  ApiFollowStatus,
  ApiListeningHistoryItem,
  ApiMusicHome,
  ApiPageResult,
  ApiSystemCapabilities,
  ApiTrack,
  ApiTrackComment,
  ApiUserSummary,
  ApiUpdateAdminTrack,
  ApiUploadPrecheckFile,
  ApiUploadPrecheckRequest,
  ApiUploadPrecheckResponse,
  ApiUploadPrecheckResult,
} from './api.generated'

// ============================================================================
// 一、契约类型（自动生成，此处只做命名收口）
// ============================================================================

/** 分页包装：后端所有分页接口统一结构。 */
export type { ApiPageResult }

/** 曲目完整 DTO。 */
export type { ApiTrack }

/** 艺人精简引用。 */
export type ArtistRef = ApiArtistSummary

/** 专辑精简引用。 */
export type AlbumRef = ApiAlbumSummary

/** 音频文件技术元信息。 */
export type AudioInfo = ApiAudio

/** 当前登录用户资料（后端 CurrentUserDto）。 */
export type ApiMe = ApiCurrentUser

/** 认证双令牌（后端 AuthTokenResponse）。 */
export type ApiAuthTokens = ApiAuthToken

/** 首页聚合 Feed（后端 MusicHomeDto）。 */
export type ApiMusicHomeFeed = ApiMusicHome

/** 系统能力开关矩阵（后端 SystemCapabilitiesDto）。 */
export type ApiCapabilities = ApiSystemCapabilities

/** 邀请令牌校验结果（后端 InvitationValidationResponse）。 */
export type { ApiInvitationValidation }

/** 上传前预检的单个文件指纹。 */
export type { ApiUploadPrecheckFile as ApiPrecheckFile }

/** 上传前预检命中的曲目摘要。 */
export type { ApiPrecheckMatch }

/** 单个文件的预检结果。 */
export type { ApiUploadPrecheckResult as ApiPrecheckResult }

/** 批量预检请求。 */
export type { ApiUploadPrecheckRequest as ApiPrecheckRequest }

/** 批量预检响应。 */
export type { ApiUploadPrecheckResponse as ApiPrecheckResponse }

/** 管理端曲目元数据更新请求（后端 UpdateAdminTrackRequest）。 */
export type ApiAdminTrackUpdate = ApiUpdateAdminTrack

// 以下类型生成名与历史名一致，直接转发；业务代码无需关心它们来自哪个文件。
export type {
  ApiAlbumDetail,
  ApiArtistDetail,
  ApiCollectionDetail,
  ApiCollectionSummary,
  ApiCreateLyric,
  ApiErrorDetail,
  ApiErrorResponse,
  ApiInvitation,
  ApiNotification,
  ApiPlaylistDetail,
  ApiPlaylistSummary,
  ApiReferencedTrack,
  ApiRejectedUpload,
  ApiQueuedUpload,
  ApiSongRequestTimelineItem,
  ApiUpdateLyric,
  ApiUserLibraryItem,
  ApiUserUploadResponse,
  ApiDailyRecommendation,
  ApiUnreadNotificationCount,
} from './api.generated'

// ============================================================================
// 一之补充、社交/收听契约类型（自动生成，此处命名收口）
// ============================================================================

/** 曲目评论（含嵌套回复）。 */
export type ApiComment = ApiTrackComment

/** 发表评论/回复的请求体。 */
export type { ApiCreateComment }

/** 评论作者/关注列表里的用户公开摘要。 */
export type ApiUserBrief = ApiUserSummary

/** 关注关系状态快照（是否已关注 + 粉丝/关注数）。 */
export type ApiFollowState = ApiFollowStatus

/** 播放历史单条（曲目 + 最近收听 + 累计次数）。 */
export type ApiHistoryItem = ApiListeningHistoryItem

// ============================================================================
// 二、纯前端类型（后端没有对应 DTO）
// ============================================================================

// ---------------------------------------------------------------------------
// 2.1 封闭集合的字面量联合
//
// 后端目前用 `string` 承载这三个封闭集合（响应 DTO 走 EF 投影，改成 enum
// 会波及查询层，见 docs/contract-audit.md 的后续项）。这里在「前端唯一契约
// 入口」把它们收窄成字面量联合，pages/ 因此获得穷尽匹配能力；后端新增取值
// 时，重新生成契约会与本文件产生 diff，必须同步追加联合成员。
// 取值必须与后端 SongRequestStatuses / LyricFormats 常量逐一对齐。
// ---------------------------------------------------------------------------

/**
 * 求歌记录状态枚举（后端状态机的字符串字面量）。
 *
 * - open：待安排，新建需求的初始状态
 * - planned：已计划，运营确认纳入排期
 * - in_progress：处理中，正在采购/入库
 * - fulfilled：已入库，已匹配到正式曲目（matchedTrackId 有值）
 * - declined：暂不收录，运营拒绝
 * - withdrawn：已撤回，提交人在受理前主动撤回（终态）
 */
export type SongRequestStatus =
  | 'open'
  | 'planned'
  | 'in_progress'
  | 'fulfilled'
  | 'declined'
  | 'withdrawn'

/** 歌词格式封闭集合：plain（纯文本）或 lrc（带时间轴）。 */
export type LyricFormat = 'plain' | 'lrc'

// ---------------------------------------------------------------------------
// 2.2 收窄后的契约类型
//
// 生成类型把这些字段声明为 string；这里映射为上面的联合，
// 使所有消费点（statusMeta / SONG_REQUEST_STATUS / 歌词表单）有编译期保护。
// ---------------------------------------------------------------------------

/** 点歌需求（后端 SongRequestDto），status 收窄为 <see cref="SongRequestStatus"/>。 */
export type ApiSongRequest = Omit<GeneratedApiSongRequest, 'status'> & {
  status: SongRequestStatus
}

/** 歌词（后端 LyricDto），format 收窄为 <see cref="LyricFormat"/>。 */
export type ApiLyric = Omit<GeneratedApiLyric, 'format'> & {
  format: LyricFormat
}

/**
 * 管理端曲目列表查询参数。
 * 后端以 [FromQuery] 标量参数接收，没有对应的 request DTO，故此处为前端自有类型。
 */
export interface ApiAdminTrackQuery {
  q?: string
  artistId?: string
  albumId?: string
  missingMetadata?: boolean
  duplicates?: boolean
  page?: number
  pageSize?: number
}

/**
 * 界面使用的扁平化曲目视图模型（非后端契约）。
 * 由 mapTrack(ApiTrack) 派生：艺人数组拼接成字符串、毫秒换算为秒、
 * 相对 URL 解析为可直接访问的地址，供播放器与列表组件直接消费。
 */
export interface Track {
  id: string
  title: string
  artist: string
  album: string
  albumId: string | null
  cover: string
  duration: number
  audioUrl: string
  genres: readonly string[]
  mood?: string
}

