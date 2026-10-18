/**
 * 类型定义模块：前端与后端 /api/v1 接口契约（Contracts）的 TypeScript 映射。
 *
 * 约定：
 * - 所有 Api* 接口与后端 DTO 一一对应，字段使用 camelCase，请勿手工分叉字段含义；
 * - 可空字段显式标注 `| null`：null 表示后端确实没有该数据（不要用空字符串/0 等默认值代替）；
 * - 分页接口统一返回 PageResult 结构（items / page / pageSize / total）；
 * - 时间字段为 ISO 8601 字符串；后端时长单位为毫秒（durationMs），界面模型 Track 中换算为秒；
 * - Track 是面向界面的扁平化视图模型，不属于后端契约，由 catalog 模块的 mapTrack 派生。
 */

/**
 * 艺人精简引用 DTO。
 * 后端在曲目、专辑等对象中以内联数组形式返回，避免前端为展示名字再发二次请求。
 */
export interface ArtistRef {
  id: string // 艺人唯一 ID
  name: string // 艺人展示名
}

/**
 * 专辑精简引用 DTO（内嵌于曲目对象）。
 */
export interface AlbumRef {
  id: string // 专辑唯一 ID
  title: string // 专辑标题
}

/**
 * 音频文件技术元信息 DTO（ApiTrack.audio）。
 * 用于管理端展示入库文件规格；audio 为 null 表示该曲目尚未关联可播放音频。
 */
export interface AudioInfo {
  container: string // 容器/封装格式，如 'mp3'、'flac'
  codec: string // 内部编码名，如 'mp3'、'flac'、'aac'
  bitrateKbps: number | null // 码率，单位 kbps；null 表示未知或无损未上报
}

/**
 * 曲目完整 DTO。对应 GET /api/v1/tracks 列表/详情、合集与歌单内嵌曲目等响应元素。
 */
export interface ApiTrack {
  id: string // 曲目唯一 ID，同时用于拼接流播放地址 /tracks/{id}/stream
  title: string // 曲目标题
  artists: ArtistRef[] // 参与艺人列表（主唱在前，后端保证至少一项）
  album: AlbumRef | null // 所属专辑；null 表示未关联专辑（单曲）
  trackNumber: number | null // 碟内曲目序号；null 表示元数据缺失
  discNumber: number | null // 碟片号；null 表示单碟或未知
  releaseYear: number | null // 发行年份（4 位）；null 表示未知
  genres: string[] // 流派/风格标签数组，无标签时为空数组
  durationMs: number | null // 曲目时长，单位毫秒；null 表示未知
  artworkUrl: string | null // 封面图片地址（可能为相对路径，需经 resolveUrl 解析）；null 表示无封面
  audio: AudioInfo | null // 音频文件元信息；null 表示尚未入库音频
}

/**
 * 管理端曲目列表查询参数，对应 GET /api/v1/admin/tracks 的 query string。
 * 所有字段可选，缺省时由后端应用默认值。
 */
export interface ApiAdminTrackQuery {
  q?: string // 标题/艺人等关键词模糊搜索
  artistId?: string // 按艺人 ID 过滤
  albumId?: string // 按专辑 ID 过滤
  missingMetadata?: boolean // true：仅返回元数据不完整的曲目
  duplicates?: boolean // true：仅返回疑似重复的曲目
  page?: number // 页码，从 1 开始
  pageSize?: number // 每页条数
}

/**
 * 管理端曲目元数据更新请求体，对应 PATCH /api/v1/admin/tracks/{trackId}。
 * 字段全部可选，仅传需要修改的字段（局部更新）。
 */
export interface ApiAdminTrackUpdate {
  title?: string // 新标题
  artists?: string[] // 新的艺人 ID 列表（整体替换，不是名字）
  trackNumber?: number // 新碟内序号
  discNumber?: number // 新碟片号
  releaseYear?: number // 新发行年份
  genres?: string[] // 新流派标签列表（整体替换）
  artworkId?: string // 新封面 ID（关联 Artworks 表记录）
}

/**
 * 通用分页结果包装结构 PageResult<T>，后端所有分页接口统一使用。
 */
export interface ApiPageResult<T> {
  items: T[] // 当前页的数据列表
  page: number // 当前页码，从 1 开始
  pageSize: number // 每页条数（请求值，可能被后端钳制）
  total: number // 符合条件的总条数，用于计算总页数
}

/**
 * 歌词 DTO，对应后端 LyricDto。一首曲目可挂多份歌词（多语言/多格式）。
 */
export interface ApiLyric {
  id: string // 歌词唯一 ID
  trackId: string // 所属曲目 ID
  language: string | null // 语言代码（如 zh-CN、en）；null 表示未知
  format: 'plain' | 'lrc' // 歌词格式：plain 纯文本 / lrc 带时间轴
  content: string // 歌词正文
  isDefault: boolean // 是否为该曲目默认展示歌词
  updatedAtUtc: string // 最后更新时间 ISO 8601
}

/**
 * 创建歌词请求体，对应 POST /api/v1/admin/tracks/{trackId}/lyrics。
 */
export interface ApiCreateLyric {
  language?: string | null // 语言代码；不传或空表示未知
  format?: 'plain' | 'lrc' // 歌词格式，默认 plain
  content: string // 歌词正文，必填
  isDefault?: boolean // 是否设为默认歌词
}

/**
 * 更新歌词请求体，对应 PUT /api/v1/admin/lyrics/{lyricId}。
 * 所有字段可选，仅传需要修改的字段。
 */
export interface ApiUpdateLyric {
  language?: string | null // 语言代码；传空字符串表示清空
  format?: 'plain' | 'lrc' // 歌词格式
  content?: string // 歌词正文
  isDefault?: boolean // 是否设为默认歌词
}

/**
 * 运营精选合集摘要 DTO，对应 GET /api/v1/collections 列表项。
 */
export interface ApiCollectionSummary {
  id: string // 合集唯一 ID
  title: string // 合集标题
  description: string | null // 合集简介；null 表示未填写
  artworkUrl: string | null // 合集封面；null 表示无封面
  trackCount: number // 合集中包含的曲目数量
}

/**
 * 精选合集详情 DTO，对应 GET /api/v1/collections/{id}。
 */
export interface ApiCollectionDetail {
  id: string // 合集唯一 ID
  title: string // 合集标题
  description: string | null // 合集简介；null 表示未填写
  artworkUrl: string | null // 合集封面；null 表示无封面
  tracks: ApiTrack[] // 合集内的完整曲目列表（含全部曲目字段）
}

/**
 * 专辑详情 DTO，对应 GET /api/v1/albums/{id}。
 */
export interface ApiAlbumDetail {
  id: string // 专辑唯一 ID
  title: string // 专辑标题
  releaseYear: number | null // 发行年份；null 表示未知
  artists: ArtistRef[] // 专辑艺人列表（合辑可能有多个）
  artworkUrl: string | null // 专辑封面；null 表示无封面
  tracks: ApiTrack[] // 专辑下的完整曲目列表
}

/**
 * 艺人详情 DTO，对应 GET /api/v1/artists/{id}。
 */
export interface ApiArtistDetail {
  id: string // 艺人唯一 ID
  name: string // 艺人名
  biography: string | null // 艺人简介/小传；null 表示未填写
  albums: AlbumRef[] // 该艺人的专辑摘要列表
  tracks: ApiTrack[] // 该艺人的曲目列表
}

/**
 * 首页聚合 Feed DTO，对应 GET /api/v1/music/home（匿名可访问）。
 */
export interface ApiMusicHomeFeed {
  featured: ApiTrack | null // 运营推荐曲目；null 时前端回退为最近新增的第一首
  collections: ApiCollectionSummary[] // 精选合集摘要列表
  recentlyAdded: ApiPageResult<ApiTrack> // 最近新增曲目分页（首页消费第一页）
}

/**
 * 求歌（歌曲需求）记录 DTO，对应 /api/v1/song-requests 系列接口。
 */
export interface ApiSongRequest {
  id: string // 需求记录唯一 ID
  title: string // 期望的曲目标题
  artistName: string // 期望的艺人名（用户手填文本）
  albumTitle: string | null // 期望的专辑名；null 表示用户未提供
  status: SongRequestStatus // 当前处理状态，取值见 SongRequestStatus
  voteCount: number // 累计票数
  myVote: boolean // 当前请求用户是否已投票（匿名访问恒为 false）
  matchedTrackId: string | null // 已匹配并入库的曲目 ID；null 表示尚未入库
  createdAt: string // 创建时间，ISO 8601 字符串
  updatedAt: string // 最近更新（状态变更/投票）时间，ISO 8601 字符串
  createdByMe: boolean // 是否由当前登录用户提交（用于判断能否撤回/重新提交）
}

/**
 * 用户自建歌单摘要 DTO，对应 GET /api/v1/me/playlists 列表项。
 */
export interface ApiPlaylistSummary {
  id: string // 歌单唯一 ID
  name: string // 歌单名称
  description: string | null // 歌单描述；null 表示未填写
  trackCount: number // 歌单内曲目数量
  updatedAt: string // 最近更新时间，ISO 8601 字符串
  artworkUrl: string | null // 歌单封面；null 表示无自定义封面
}

/**
 * 用户歌单详情 DTO，对应 GET /api/v1/me/playlists/{id}。
 */
export interface ApiPlaylistDetail {
  id: string // 歌单唯一 ID
  name: string // 歌单名称
  description: string | null // 歌单描述；null 表示未填写
  tracks: ApiTrack[] // 歌单内完整曲目列表
  updatedAt: string // 最近更新时间，ISO 8601 字符串
  artworkUrl: string | null // 歌单封面；null 表示无自定义封面
}

/**
 * 当前登录用户资料 DTO，对应 GET /api/v1/me。
 */
export interface ApiMe {
  id: string // 用户唯一 ID
  email: string // 登录邮箱（同时为邀请绑定邮箱）
  displayName: string // 展示昵称
  roles: string[] // 角色码数组，包含 'admin' 即为管理员（前端仅用于入口显隐）
  avatarUrl: string | null // 已上传头像的读取地址（带版本参数）；未上传时为 null，前端回退为首字母头像
}

/**
 * 认证双令牌 DTO，对应 POST /api/v1/auth/login 与 POST /api/v1/auth/refresh 的成功响应。
 */
export interface ApiAuthTokens {
  accessToken: string // 短期访问令牌，放入 Authorization: Bearer 头
  refreshToken: string // 长期刷新令牌，仅用于 /auth/refresh 与 /auth/logout
  tokenType: string // 令牌类型，固定为 'Bearer'
  expiresIn: number // accessToken 有效期，单位秒（refresh 轮换时间由服务端掌握）
}

/**
 * 邀请令牌校验结果 DTO，对应 POST /api/v1/auth/invitations/validate。
 */
export interface ApiInvitationValidation {
  email: string // 该邀请绑定的邮箱（注册时需沿用）
  expiresAt: string // 邀请到期时间，ISO 8601 字符串
}

/**
 * 系统能力开关矩阵 DTO，对应 GET /api/v1/system/capabilities（匿名可访问）。
 * 仅决定前端功能入口的显隐；真正的权限与限制始终由服务端强制执行。
 */
export interface ApiCapabilities {
  anonymousPlayback: boolean // 是否允许匿名试听
  userCollections: boolean // 是否开放用户自建歌单
  songRequests: boolean // 是否开放求歌功能
  adminLibraryImport: boolean // 是否开放管理员曲库导入
  manualReview: boolean // 是否启用人工复核工作流
  editorialCollections: boolean // 是否展示运营精选合集
  listeningEvents: boolean // 是否上报收听事件（统计/推荐用）
  personalizedRecommendations: boolean // 是否提供个性化推荐
  serverTranscoding: boolean // 服务端是否支持转码/按需串流
  userUploads: boolean // 是否开放用户上传本地音频（默认私有、可立即收听、管理员审核转公开）
  allowedFormats: string[] // 允许上传/播放的音频格式扩展名（不含点，如 'mp3'、'flac'）
  maxUploadSizeBytes: number | null // 单文件上传大小上限，单位字节；null 表示不限制
}

/**
 * “我的曲库”条目：当前用户引用的曲目（含私有与公开），对应后端 UserLibraryItemDto。
 * 私有曲目仅上传者本人可见与可播放；公开曲目任何用户均可访问。
 */
export interface ApiUserLibraryItem {
  trackId: string // 曲目 ID
  title: string // 曲目标题
  artists: string[] // 艺人名列表
  albumTitle: string | null // 专辑标题；无专辑时为 null
  durationMs: number | null // 时长（毫秒）
  artworkUrl: string | null // 封面图 URL；无封面时为 null
  isPublic: boolean // 是否已公开（私有曲目仅本人可见）
  addedAtUtc: string // 加入曲库时间 ISO 8601（UTC）
}

/**
 * 上传前预检的单个文件指纹，由客户端读取本地音频标签得到。
 */
export interface ApiPrecheckFile {
  fileName: string // 本地文件名
  sizeBytes?: number | null // 文件大小（字节）；读取失败时省略
  title?: string | null // 标签标题；无标签时省略
  artist?: string | null // 首位艺人；无标签时省略
  durationMs?: number | null // 时长（毫秒）；读取失败时省略
}

/** 批量预检请求：上传前把本地文件指纹批量发给服务端查重。 */
export interface ApiPrecheckRequest {
  files: ApiPrecheckFile[]
}

/** 预检命中的曲目摘要。 */
export interface ApiPrecheckMatch {
  trackId: string // 曲目 ID
  title: string // 曲目标题
  artists: string[] // 艺人名列表
  isPublic: boolean // 是否已公开（公开曲目可被任何用户引用）
  artworkUrl: string | null // 封面图 URL；无封面时为 null
}

/** 单个文件的预检结果：match 为命中曲目，未命中为 null（客户端应上传该文件）。 */
export interface ApiPrecheckResult {
  fileName: string // 与请求对应的本地文件名
  match: ApiPrecheckMatch | null
}

/** 批量预检响应。 */
export interface ApiPrecheckResponse {
  results: ApiPrecheckResult[]
}

/** 上传响应：已引用（内容哈希命中，秒级加入曲库）的曲目。 */
export interface ApiReferencedTrack {
  trackId: string
  title: string
  artists: string[]
}

/** 上传响应：进入处理队列的新歌（转码 + 歌词自动匹配完成后私有入库）。 */
export interface ApiQueuedUpload {
  itemId: string // 导入条目 ID
  displayName: string // 文件名
}

/** 上传响应：无法引用/无法处理的文件（如他人私有曲目）。 */
export interface ApiRejectedUpload {
  displayName: string // 文件名
  reason: string // 拒绝原因（如 private_track_of_another_user）
}

/** 用户上传响应。 */
export interface ApiUserUploadResponse {
  batchId: string | null // 处理批次 ID；全部引用时为 null
  referenced: ApiReferencedTrack[] // 已引用（秒级加入曲库）
  queued: ApiQueuedUpload[] // 处理中（新歌走转码与歌词匹配）
  rejected: ApiRejectedUpload[] // 被拒绝的文件
}

/**
 * 求歌记录状态枚举（后端状态机的字符串字面量）。
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

/**
 * 界面使用的扁平化曲目视图模型（非后端 DTO）。
 * 由 mapTrack(ApiTrack) 派生：把艺人数组拼接成字符串、毫秒换算为秒、
 * 相对 URL 解析为可直接访问的地址，供播放器与列表组件直接消费。
 */
export interface Track {
  id: string // 曲目 ID（与 ApiTrack.id 一致）
  title: string // 曲目标题
  artist: string // 拼接后的艺人名（', ' 连接），无艺人时为 '未知艺人'
  album: string // 专辑名，无专辑时为空字符串 ''
  albumId: string | null // 专辑 ID；null 表示无专辑，用于跳转控制
  cover: string // 可直接使用的封面 URL，无封面时为空字符串 ''
  duration: number // 时长，单位秒（由 durationMs / 1000 换算）
  audioUrl: string // 可直接播放的流地址（/tracks/{id}/stream 经 resolveUrl 解析）
  genres: readonly string[] // 流派标签（只读，沿用 DTO 数组）
  mood?: string // UI 派生的情绪标签（从流派推断），可选
}
