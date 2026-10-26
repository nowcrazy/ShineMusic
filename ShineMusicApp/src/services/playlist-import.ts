/**
 * 歌单解析导入服务：粘贴外部平台歌单链接 → 解析预览 → 导入生成新歌单。
 *
 * 对齐后端 /api/v1/me/playlist-imports 端点（登录用户可用）：
 * - POST /parse   解析歌单，逐曲标记是否已匹配曲库
 * - POST /import  导入生成新歌单，未匹配曲目后台静默下载补齐
 */
import { request } from './http'

/** 预览曲目条目（后端 PlaylistImportPreviewTrackDto） */
export interface ApiPlaylistImportPreviewTrack {
  title: string
  artists: string[]
  albumTitle: string | null
  durationMs: number | null
  /** 是否已匹配曲库（可直接入歌单） */
  matched: boolean
  matchedTrackId: string | null
}

/** 解析预览响应（后端 PlaylistImportParseResponse） */
export interface ApiPlaylistImportParse {
  platform: string
  name: string
  coverUrl: string | null
  total: number
  tracks: ApiPlaylistImportPreviewTrack[]
}

/** 导入结果（后端 PlaylistImportResponse） */
export interface ApiPlaylistImportResult {
  playlistId: string
  total: number
  matched: number
  pending: number
}

/** 解析歌单预览（外部平台请求较慢，超时放宽到 60s）。 */
export async function parsePlaylistImport(url: string): Promise<ApiPlaylistImportParse> {
  return request<ApiPlaylistImportParse>('POST', '/api/v1/me/playlist-imports/parse', { url }, { timeoutMs: 60000 })
}

/** 导入歌单：立即生成新歌单并触发静默下载（含逐曲匹配与建任务，超时放宽到 180s）。 */
export async function importPlaylist(url: string, name?: string): Promise<ApiPlaylistImportResult> {
  return request<ApiPlaylistImportResult>('POST', '/api/v1/me/playlist-imports/import', { url, name }, { timeoutMs: 180000 })
}
