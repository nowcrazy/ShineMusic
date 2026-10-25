/**
 * 播放历史服务：当前用户按曲目聚合的最近收听记录。
 *
 * 对齐后端 /api/v1 下的端点：
 * - 播放历史：GET /me/history?page=&pageSize=（鉴权）
 *
 * 后端按曲目聚合：同曲目只显示一条（最近收听时间 + 累计播放次数）。
 */
import type { ApiHistoryItem, ApiPageResult, Track } from '@/types/music'
import { API_PREFIX, http } from './http'
import { mapTrack } from './catalog'

/** 播放历史单条（曲目已扁平化映射，供播放器与列表直接消费）。 */
export interface HistoryEntry {
  track: Track
  /** 最近一次收听时间（ISO 字符串） */
  lastPlayedAt: string
  /** 累计播放次数 */
  playCount: number
}

/**
 * 获取当前用户的播放历史（分页）。
 * GET /me/history?page=&pageSize=（鉴权）
 * @param page 页码（默认 1）
 * @param pageSize 每页条数（默认 20）
 * @returns items 已把曲目映射为前端 Track
 */
export async function getListeningHistory(page = 1, pageSize = 20) {
  const result = await http.get<ApiPageResult<ApiHistoryItem>>(
    `${API_PREFIX}/me/history?page=${page}&pageSize=${pageSize}`,
  )
  const items: HistoryEntry[] = result.items.map((item) => ({
    track: mapTrack(item.track),
    lastPlayedAt: item.lastPlayedAtUtc,
    playCount: item.playCount,
  }))
  return { ...result, items }
}