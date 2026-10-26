/**
 * 我的音乐库服务：收藏与歌单。
 *
 * 对齐后端 /api/v1 下的两组端点：
 * - 收藏：  GET    /me/favorites            （拉取收藏列表）
 *           PUT    /me/favorites/{trackId}  （加入收藏）
 *           DELETE /me/favorites/{trackId}  （取消收藏）
 * - 歌单：  GET    /me/playlists             （歌单列表）
 *           POST   /me/playlists             （新建歌单）
 *           GET    /me/playlists/{id}        （歌单详情）
 *           PATCH  /me/playlists/{id}        （重命名/改描述/封面）
 *           DELETE /me/playlists/{id}        （删除歌单）
 *           PUT    /me/playlists/{id}/tracks/{trackId}  （加入曲目）
 *           DELETE /me/playlists/{id}/tracks/{trackId}  （移除曲目）
 *
 * 本模块使用 module 级 ref 维护收藏状态，供全局复用；曲目对象通过
 * catalog.mapTrack 统一从后端 ApiTrack 映射为前端 Track，保证收藏与
 * 目录列表的曲目结构一致。
 */
import { computed, ref } from 'vue'
import type {
  ApiPageResult,
  ApiPlaylistDetail,
  ApiPlaylistSummary,
  ApiTrack,
  Track,
} from '@/types/music'
import { API_PREFIX, http } from './http'
import { mapTrack } from './catalog'

// ---- 收藏状态（module 级单例，全局共享） ----
// 已收藏曲目的 id 集合，用于 O(1) 判断是否收藏
export const favoriteIds = ref<Set<string>>(new Set())
// 收藏列表是否已加载完成
const favoritesLoaded = ref(false)
// 收藏列表是否正在加载中（用于防重入）
const favoritesLoading = ref(false)
// 已收藏曲目列表（已通过 mapTrack 映射为前端 Track）
export const favoriteTracks = ref<Track[]>([])

/**
 * 判断某曲目是否已收藏，返回响应式计算属性。
 *
 * @param trackId 曲目 id
 * @returns 当 favoriteIds 包含 trackId 时为 true
 */
export const isFavorite = (trackId: string) => computed(() => favoriteIds.value.has(trackId))

/**
 * 拉取当前用户的收藏列表。
 *
 * 默认幂等：已加载或加载中且非 force 时直接返回，避免重复请求。
 * 请求成功后同步更新 favoriteIds / favoriteTracks / favoritesLoaded。
 *
 * @param force 是否强制重新加载（忽略已加载状态）
 * @returns 无返回值，结果写入 module 级 ref
 * @throws 网络或后端错误
 */
export async function loadFavorites(force = false) {
  if ((favoritesLoaded.value || favoritesLoading.value) && !force) return
  favoritesLoading.value = true
  try {
    // GET /me/favorites?pageSize=100
    const result = await http.get<ApiPageResult<ApiTrack>>(
      `${API_PREFIX}/me/favorites?pageSize=100`,
    )
    // 复用 catalog.mapTrack，确保收藏曲目与目录曲目结构一致
    favoriteTracks.value = result.items.map(mapTrack)
    favoriteIds.value = new Set(result.items.map((track) => track.id))
    favoritesLoaded.value = true
  } finally {
    favoritesLoading.value = false
  }
}

/**
 * 清空本地收藏状态（登出或切换用户时调用）。
 *
 * 副作用：重置 favoriteIds、favoriteTracks、favoritesLoaded，不发起请求。
 */
export function resetLibrary() {
  favoriteIds.value = new Set()
  favoriteTracks.value = []
  favoritesLoaded.value = false
}

/**
 * 切换收藏状态（加入/取消收藏），采用乐观更新，失败回滚。
 *
 * - 已收藏 → 调用 DELETE /me/favorites/{trackId}
 * - 未收藏 → 调用 PUT /me/favorites/{trackId}
 *
 * @param track 目标曲目对象
 * @returns 操作后的最新收藏状态（true=已收藏）
 * @throws 请求失败时回滚本地状态并抛出错误
 */
export async function toggleFavorite(track: Track): Promise<boolean> {
  const liked = favoriteIds.value.has(track.id)
  // 乐观更新，失败回滚
  const next = new Set(favoriteIds.value)
  if (liked) next.delete(track.id)
  else next.add(track.id)
  favoriteIds.value = next
  favoriteTracks.value = liked
    ? favoriteTracks.value.filter((item) => item.id !== track.id)
    : [track, ...favoriteTracks.value]

  try {
    if (liked) {
      await http.delete(`${API_PREFIX}/me/favorites/${track.id}`)
    } else {
      await http.put(`${API_PREFIX}/me/favorites/${track.id}`)
    }
    return !liked
  } catch (error) {
    const rollback = new Set(favoriteIds.value)
    if (liked) rollback.add(track.id)
    else rollback.delete(track.id)
    favoriteIds.value = rollback
    favoriteTracks.value = liked
      ? [track, ...favoriteTracks.value]
      : favoriteTracks.value.filter((item) => item.id !== track.id)
    throw error
  }
}

/**
 * 收藏相关组合式 API，供 setup 中解构使用。
 *
 * @returns 收藏状态与方法的集合
 */
export function useFavorites() {
  return {
    favoriteIds,
    favoriteTracks,
    favoritesLoaded,
    loadFavorites,
    toggleFavorite,
    resetLibrary,
  }
}

// ---- 歌单 ----

/**
 * 获取当前用户的歌单列表。
 *
 * GET /me/playlists?pageSize=100
 *
 * @returns 歌单摘要分页结果
 */
export async function getPlaylists() {
  return http.get<ApiPageResult<ApiPlaylistSummary>>(`${API_PREFIX}/me/playlists?pageSize=100`)
}

/**
 * 新建歌单。
 *
 * POST /me/playlists
 *
 * @param name 歌单名称
 * @param description 歌单描述（可选）
 * @param artworkId 封面图片 id（可选）
 * @returns 新建歌单的摘要信息
 */
export async function createPlaylist(name: string, description?: string, artworkId?: string) {
  return http.post<ApiPlaylistSummary>(`${API_PREFIX}/me/playlists`, { name, description, artworkId })
}

/**
 * 获取歌单详情及其曲目列表。
 *
 * GET /me/playlists/{playlistId}
 *
 * @param playlistId 歌单 id
 * @returns detail 为后端歌单详情，tracks 为经 mapTrack 映射后的曲目数组
 */
export async function getPlaylist(playlistId: string) {
  const detail = await http.get<ApiPlaylistDetail>(`${API_PREFIX}/me/playlists/${playlistId}`)
  return { detail, tracks: detail.tracks.map(mapTrack) }
}

/**
 * 移除歌单中的一条待补齐（pending）曲目。
 *
 * DELETE /me/playlists/{playlistId}/pending/{pendingId}
 *
 * @param playlistId 歌单 id
 * @param pendingId 待补齐条目 id
 */
export async function removePlaylistPendingTrack(playlistId: string, pendingId: string) {
  return http.delete<void>(`${API_PREFIX}/me/playlists/${playlistId}/pending/${pendingId}`)
}

/**
 * 更新歌单信息（名称/描述/封面）。
 *
 * PATCH /me/playlists/{playlistId}
 *
 * @param playlistId 歌单 id
 * @param name 新歌单名称
 * @param description 新歌单描述（可选）
 * @param artworkId 新封面图片 id（可选）
 * @param clearArtwork 是否清空封面（为 true 时移除现有封面）
 */
export async function renamePlaylist(
  playlistId: string,
  name: string,
  description?: string,
  artworkId?: string,
  clearArtwork = false,
) {
  return http.patch(`${API_PREFIX}/me/playlists/${playlistId}`, {
    name,
    description,
    artworkId,
    clearArtwork,
  })
}

/**
 * 删除歌单。
 *
 * DELETE /me/playlists/{playlistId}
 *
 * @param playlistId 歌单 id
 */
export async function deletePlaylist(playlistId: string) {
  return http.delete(`${API_PREFIX}/me/playlists/${playlistId}`)
}

/**
 * 向歌单添加曲目。
 *
 * PUT /me/playlists/{playlistId}/tracks/{trackId}
 *
 * @param playlistId 歌单 id
 * @param trackId 曲目 id
 */
export async function addPlaylistTrack(playlistId: string, trackId: string) {
  return http.put(`${API_PREFIX}/me/playlists/${playlistId}/tracks/${trackId}`)
}

/**
 * 从歌单移除曲目。
 *
 * DELETE /me/playlists/{playlistId}/tracks/{trackId}
 *
 * @param playlistId 歌单 id
 * @param trackId 曲目 id
 */
export async function removePlaylistTrack(playlistId: string, trackId: string) {
  return http.delete(`${API_PREFIX}/me/playlists/${playlistId}/tracks/${trackId}`)
}
