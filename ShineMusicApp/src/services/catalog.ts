/**
 * 公共曲库目录 API 模块（匿名可读），对接后端 /api/v1 音乐目录契约。
 *
 * 覆盖接口：
 * - GET /music/home              首页聚合 Feed
 * - GET /tracks                  曲目搜索与条件分页列表
 * - GET /genres                  流派集合
 * - GET /collections[/{id}]      运营精选合集列表/详情
 * - GET /albums/{id}             专辑详情
 * - GET /artists/{id}            艺人详情
 * - GET /tracks/{id}/stream      曲目流播放地址（在 mapTrack 中拼接）
 *
 * 另外提供：
 * - mapTrack：后端 ApiTrack -> 界面扁平模型 Track 的纯函数映射器。
 *
 * 数据策略：不做任何内置演示数据兜底。后端没有已发布曲目时一律返回空集合，
 * 由页面展示空态；请求失败（网络错误或业务错误）直接抛给调用方处理。
 */
import type {
  ApiAlbumDetail,
  ApiArtistDetail,
  ApiCollectionDetail,
  ApiCollectionSummary,
  ApiLyric,
  ApiMusicHomeFeed,
  ApiPageResult,
  ApiTrack,
  Track,
} from '@/types/music'
import { API_PREFIX, http, resolveUrl } from './http'
import { t } from '@/locale'

/**
 * 把后端曲目 DTO 映射为界面扁平视图模型 Track（纯函数）。
 * 处理：艺人数组拼接名字、缺失专辑兜底、封面与流地址经 resolveUrl 解析、
 * 毫秒时长换算为秒、从流派推断情绪标签 mood。
 * @param track 后端 ApiTrack DTO
 * @returns 可被播放器与列表组件直接消费的 Track
 */
export function mapTrack(track: ApiTrack): Track {
  const mood =
    track.genres.find((genre) => ['松弛', '轻快', '温柔', '夜晚'].includes(genre)) ??
    track.genres[0] ??
    '全部'

  return {
    id: track.id,
    title: track.title,
    artist: track.artists.map((artist) => artist.name).join(', ') || t('catalog.unknownArtist'),
    album: track.album?.title ?? '',
    albumId: track.album?.id ?? null,
    cover: track.artworkUrl ? resolveUrl(track.artworkUrl) : '',
    duration: (track.durationMs ?? 0) / 1000,
    audioUrl: resolveUrl(`${API_PREFIX}/tracks/${track.id}/stream`),
    genres: track.genres,
    mood,
  }
}

/** 首页聚合视图模型（在 ApiMusicHomeFeed 基础上补全界面分区与扁平曲目） */
export interface HomeFeed {
  featured: Track | null // 焦点推荐曲目
  featuredCollectionId: string | null // 焦点关联的合集 ID（用于“查看合集”），无则 null
  collections: readonly ApiCollectionSummary[] // 精选合集摘要列表
  recentlyAdded: Track[] // 最近新增（映射后）
  dailyMix: Track[] // 每日推荐分区（无数据时为空数组）
  trending: Track[] // 热门趋势分区（最近新增前 4 条，无数据时为空数组）
}

/**
 * 获取首页聚合 Feed。
 * - 请求：GET /api/v1/music/home（匿名）
 * - 映射：recentlyAdded 整体转 Track；featured 缺失时回退最近新增第一首；
 *   dailyMix/trending 直接取真实数据，无曲目时为空数组（不做演示数据兜底）。
 * @returns 首页视图模型
 * @throws 请求失败（网络错误或后端业务错误）时抛出 ApiError，由页面展示错误态
 */
// ============ 模块级内存缓存（stale-while-revalidate）============
// 应用从后台切回前台时 Vue 组件会重建，onMounted 再次触发。
// 高频读接口加内存缓存：首次发请求，后续先返回缓存再静默刷新，
// 避免用户看到 loading。进程被系统回收后缓存自然清空，冷启动会重新请求。
let _homeFeedCache: HomeFeed | null = null
let _homeFeedInFlight: Promise<HomeFeed> | null = null

let _genresCache: readonly string[] | null = null
let _genresInFlight: Promise<readonly string[]> | null = null

async function fetchHomeFeedRaw(): Promise<HomeFeed> {
  const home = await http.get<ApiMusicHomeFeed>(`${API_PREFIX}/music/home`, false)
  const recentTracks = home.recentlyAdded.items.map(mapTrack)
  return {
    featured: home.featured ? mapTrack(home.featured) : recentTracks[0] ?? null,
    featuredCollectionId: home.collections[0]?.id ?? null,
    collections: home.collections,
    recentlyAdded: recentTracks,
    dailyMix: recentTracks,
    trending: recentTracks.slice(0, 4),
  }
}

async function refreshHomeFeedSilently(): Promise<void> {
  try {
    _homeFeedCache = await fetchHomeFeedRaw()
  } catch {
    // 静默失败，保留旧缓存
  }
}

/**
 * 获取首页聚合 Feed（带模块级内存缓存）。
 * - 首次调用：发网络请求，缓存结果后返回。
 * - 后续调用：**立即返回缓存**，同时后台静默刷新（stale-while-revalidate）。
 *   组件重建 / 从后台恢复 / 快速重复调用都不会再看到 loading。
 * - 并发去重：请求进行中时再次调用会复用同一个 Promise。
 */
export async function getHomeFeed(): Promise<HomeFeed> {
  if (_homeFeedCache) {
    void refreshHomeFeedSilently()
    return _homeFeedCache
  }
  if (!_homeFeedInFlight) {
    _homeFeedInFlight = fetchHomeFeedRaw().finally(() => {
      _homeFeedInFlight = null
    })
  }
  _homeFeedCache = await _homeFeedInFlight
  return _homeFeedCache
}

/** 清空首页缓存（管理员发布/修改合集后手动触发）。 */
export function invalidateHomeFeedCache(): void {
  _homeFeedCache = null
}

/**
 * 同步读取首页缓存（页面 onMounted 里先用它同步填充 ref，避免 loading 闪烁）。
 * 缓存未就绪时返回 null。
 */
export function peekHomeFeedCache(): HomeFeed | null {
  return _homeFeedCache
}

/** 同步读取流派缓存（未就绪返回 null）。 */
export function peekGenresCache(): readonly string[] | null {
  return _genresCache
}

/**
 * 关键词搜索曲目。
 * - 请求：GET /api/v1/tracks?q={keyword}&pageSize=50（匿名，关键词经 encodeURIComponent）
 * @param query 原始关键词，内部 trim；空白关键词直接返回 []
 * @returns 匹配曲目（映射后，最多 50 条；无匹配时为空数组）
 * @throws 请求失败时抛出 ApiError，由调用方展示错误态
 */
export async function searchTracks(query: string): Promise<Track[]> {
  const keyword = query.trim()
  if (!keyword) return []
  const result = await http.get<ApiPageResult<ApiTrack>>(
    `${API_PREFIX}/tracks?q=${encodeURIComponent(keyword)}&pageSize=50`,
    false,
  )
  return result.items.map(mapTrack)
}

/** 曲目列表查询条件（GET /api/v1/tracks 的前端模型） */
export interface TrackQuery {
  artistId?: string // 按艺人过滤
  albumId?: string // 按专辑过滤
  genre?: string // 按流派过滤
  sort?: 'recent' | 'title' | 'duration' // 排序方式：最近新增 / 标题 / 时长
  page?: number // 页码，默认 1
  pageSize?: number // 每页条数，默认 20
}

/**
 * 按条件分页获取曲目列表。
 * - 请求：GET /api/v1/tracks?artistId=&albumId=&genre=&sort=&page=&pageSize=（匿名）
 * @param query 过滤/排序/分页条件，全可选
 * @returns 分页结构，其中 items 已映射为界面 Track
 */
export async function getTracks(query: TrackQuery = {}): Promise<ApiPageResult<Track>> {
  const params = new URLSearchParams()
  if (query.artistId) params.set('artistId', query.artistId)
  if (query.albumId) params.set('albumId', query.albumId)
  if (query.genre) params.set('genre', query.genre)
  if (query.sort) params.set('sort', query.sort)
  params.set('page', String(query.page ?? 1))
  params.set('pageSize', String(query.pageSize ?? 20))
  const result = await http.get<ApiPageResult<ApiTrack>>(
    `${API_PREFIX}/tracks?${params.toString()}`,
    false,
  )
  return { ...result, items: result.items.map(mapTrack) }
}

/**
 * 获取单曲详情（映射为前端 Track）。
 * - 请求：GET /api/v1/tracks/{trackId}（匿名；私有曲目后端按可见性返回 404）
 * @param trackId 曲目 ID
 * @returns 映射后的 Track
 * @throws 曲目不存在（404）或网络错误时抛出 ApiError
 */
export async function getTrack(trackId: string): Promise<Track> {
  const track = await http.get<ApiTrack>(`${API_PREFIX}/tracks/${trackId}`, false)
  return mapTrack(track)
}

/**
 * 获取全部流派标签。
 * - 请求：GET /api/v1/genres?pageSize=100（匿名）
 * @returns 流派名字符串数组（取分页 items）
 */
export async function getGenres(): Promise<readonly string[]> {
  if (_genresCache) return _genresCache
  if (!_genresInFlight) {
    _genresInFlight = http
      .get<ApiPageResult<string>>(`${API_PREFIX}/genres?pageSize=100`, false)
      .then((r) => r.items)
      .finally(() => {
        _genresInFlight = null
      })
  }
  _genresCache = await _genresInFlight
  return _genresCache
}

/**
 * 获取精选合集摘要列表。
 * - 请求：GET /api/v1/collections?pageSize=50（匿名）
 * @returns 合集摘要数组
 */
export async function getCollections(): Promise<readonly ApiCollectionSummary[]> {
  const result = await http.get<ApiPageResult<ApiCollectionSummary>>(
    `${API_PREFIX}/collections?pageSize=50`,
    false,
  )
  return result.items
}

// 额外缓存：getCollection 按 ID 缓存合集详情（详情页重建时避免 loading 闪烁）
type CollectionDetailResult = { detail: ApiCollectionDetail; tracks: Track[] }
const _collectionCache = new Map<string, CollectionDetailResult>()
const _collectionInFlight = new Map<string, Promise<CollectionDetailResult>>()

/**
 * 获取精选合集详情及其映射后的曲目列表（按 ID 模块级缓存 + stale-while-revalidate）。
 * - 首次调用某 ID：发请求，缓存后返回。
 * - 后续调用同 ID：立即返回缓存，后台静默刷新。
 * - 不同 ID 独立缓存互不影响。
 */
export async function getCollection(collectionId: string): Promise<CollectionDetailResult> {
  const cached = _collectionCache.get(collectionId)
  if (cached) {
    void (async () => {
      try {
        const detail = await http.get<ApiCollectionDetail>(
          `${API_PREFIX}/collections/${collectionId}`,
          false,
        )
        _collectionCache.set(collectionId, { detail, tracks: detail.tracks.map(mapTrack) })
      } catch {
        /* 静默刷新失败 */
      }
    })()
    return cached
  }
  let inflight = _collectionInFlight.get(collectionId)
  if (!inflight) {
    inflight = http
      .get<ApiCollectionDetail>(`${API_PREFIX}/collections/${collectionId}`, false)
      .then((detail) => {
        const result = { detail, tracks: detail.tracks.map(mapTrack) }
        _collectionCache.set(collectionId, result)
        return result
      })
      .finally(() => {
        _collectionInFlight.delete(collectionId)
      })
    _collectionInFlight.set(collectionId, inflight)
  }
  return inflight
}

/**
 * 获取专辑详情、封面与曲目列表。
 * - 请求：GET /api/v1/albums/{albumId}（匿名）
 * @param albumId 专辑 ID
 * @returns detail 原始 DTO + cover 解析后的封面地址（无封面为 ''）+ tracks 扁平曲目
 */
export async function getAlbum(albumId: string) {
  const detail = await http.get<ApiAlbumDetail>(`${API_PREFIX}/albums/${albumId}`, false)
  return {
    detail,
    cover: detail.artworkUrl ? resolveUrl(detail.artworkUrl) : '',
    tracks: detail.tracks.map(mapTrack),
  }
}

/**
 * 获取艺人详情及其曲目列表。
 * - 请求：GET /api/v1/artists/{artistId}（匿名）
 * @param artistId 艺人 ID
 * @returns detail 原始 DTO（含简介、专辑摘要、曲目）+ tracks 扁平曲目数组
 */
export async function getArtist(artistId: string) {
  const detail = await http.get<ApiArtistDetail>(`${API_PREFIX}/artists/${artistId}`, false)
  return { detail, tracks: detail.tracks.map(mapTrack) }
}

/**
 * 获取指定曲目的默认歌词（播放器消费）。
 * - 请求：GET /api/v1/tracks/{trackId}/lyrics/default（匿名）
 * - 后端优先返回 IsDefault=true 的歌词，否则返回最早创建的一份。
 * @param trackId 曲目 ID
 * @returns 歌词 DTO；该曲目无歌词时接口返回 404，此处捕获后返回 null
 */
export async function getTrackDefaultLyric(trackId: string): Promise<ApiLyric | null> {
  try {
    return await http.get<ApiLyric>(`${API_PREFIX}/tracks/${trackId}/lyrics/default`, false)
  } catch {
    return null
  }
}
