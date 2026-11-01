/**
 * 歌单解析服务：粘贴外部平台歌单链接 → 解析预览（含曲库匹配标记）→ 加入下载队列或导入歌单。
 *
 * 对齐后端 /api/v1/me/playlist-imports 端点（登录用户可用，受 UserCollections 开关控制）：
 * - POST /parse   解析歌单，逐曲标记是否已匹配曲库
 * - POST /queue   未匹配曲目加入后台下载队列，不创建歌单
 * - POST /import  导入生成新歌单
 */
import { API_PREFIX, request } from './http'

/** 平台标识（与后端 IPlaylistParser.Platform 对齐） */
export type PlaylistImportPlatform = 'netease' | 'qqmusic' | 'kuwo' | 'kugou' | 'ytmusic'

/** 平台展示名（界面徽章用） */
export const PlaylistImportPlatformLabel: Record<string, string> = {
  netease: '网易云音乐',
  qqmusic: 'QQ 音乐',
  kuwo: '酷我音乐',
  kugou: '酷狗音乐',
  ytmusic: 'YouTube Music',
}

/** 预览曲目条目 */
export interface PlaylistImportPreviewTrack {
  title: string
  artists: string[]
  albumTitle: string | null
  durationMs: number | null
  /** 是否已匹配曲库（可直接入歌单） */
  matched: boolean
  matchedTrackId: string | null
}

/** 解析预览响应 */
export interface PlaylistImportParseResult {
  platform: string
  name: string
  coverUrl: string | null
  total: number
  tracks: PlaylistImportPreviewTrack[]
}

/** 导入结果 */
export interface PlaylistImportResult {
  playlistId: string
  total: number
  matched: number
  pending: number
}

export interface PlaylistDownloadQueueResult {
  total: number
  matched: number
  queued: number
}

/** 重新补齐结果：各分项处理数量 */
export interface PlaylistRematchResult {
  playlistId: string
  /** 参与检查的待处理曲目总数 */
  total: number
  /** 命中曲库直接转正的曲目数 */
  promoted: number
  /** 重新排入后台补齐队列的曲目数 */
  retried: number
  /** 仍未找到可用音源的曲目数 */
  noSource: number
  /** 下载仍在进行中、保持不动的曲目数 */
  unchanged: number
}

/** 解析歌单预览（外部平台请求较慢，超时放宽到 60s）。 */
export async function parsePlaylistImport(url: string): Promise<PlaylistImportParseResult> {
  return request<PlaylistImportParseResult>('POST', `${API_PREFIX}/me/playlist-imports/parse`, { url }, {
    timeoutMs: 60000,
  })
}

/** 将解析结果中的未入库曲目加入后台下载队列，不创建用户歌单。 */
export async function queuePlaylistDownloads(url: string): Promise<PlaylistDownloadQueueResult> {
  return request<PlaylistDownloadQueueResult>('POST', `${API_PREFIX}/me/playlist-imports/queue`, { url }, {
    timeoutMs: 120000,
  })
}

/**
 * 导入歌单：立即生成新歌单并返回。
 *
 * 后端在请求内只做「解析（复用预览结果）+ 曲库匹配 + 落库」，不做音源搜索，
 * 未匹配曲目进入后台补齐队列由 worker 异步建下载任务，因此 200+ 首也能秒级返回。
 *
 * @param downloadMissing true＝未匹配曲目加入后台下载队列（默认）；false＝仅导入歌单，不下载
 */
export async function importPlaylist(
  url: string,
  name?: string,
  downloadMissing = true,
): Promise<PlaylistImportResult> {
  return request<PlaylistImportResult>('POST', `${API_PREFIX}/me/playlist-imports/import`, {
    url,
    name,
    downloadMissing,
  }, {
    timeoutMs: 120000,
  })
}

/**
 * 重新补齐歌单中待下载/无音源/下载任务丢失的曲目。
 *
 * 后端逐条重跑曲库匹配（命中直接转正），未命中的重置为 pending 排入后台补齐队列，
 * 接口本身不做音源搜索，因此立即返回；实际补齐由后台 worker 持续进行。
 */
export async function rematchPlaylist(playlistId: string): Promise<PlaylistRematchResult> {
  return request<PlaylistRematchResult>(
    'POST',
    `${API_PREFIX}/me/playlist-imports/playlists/${playlistId}/rematch`,
    undefined,
    { timeoutMs: 120000 },
  )
}
