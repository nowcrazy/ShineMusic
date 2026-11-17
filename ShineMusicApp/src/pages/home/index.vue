<script setup lang="ts">
/**
 * 主框架：为你推荐歌单、发现站内榜单、曲库默认公开曲目、我的管理个人歌单。
 * 每日推荐与精选歌单仅使用真实数据，搜索独立于公开曲目分页。
 * 账号与管理入口收于头像抽屉，底部四项导航与播放器分离。
 * 对应后端接口：services/catalog（getHomeFeed / getGenres / searchTracks）、
 * services/library（loadFavorites / getPlaylists / createPlaylist）、services/user-library（loadMyLibrary / removeFromLibrary）、
 * services/recommend、services/player、services/capabilities 功能开关、services/session。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import {
  AudioLines,
  Bell,
  ChevronRight,
  Compass,
  Download,
  Heart,
  History,
  Inbox,
  Library,
  ListMusic,
  Music2,
  Pause,
  Play,
  Plus,
  Search,
  Sparkles,
  Trash2,
  Upload,
  UserRound,
} from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import CoverArt from '@/components/CoverArt.vue'
import ChartBrowser from '@/components/ChartBrowser.vue'
import ProfileDrawer from '@/components/ProfileDrawer.vue'
import { getGenres, getHomeFeed, getTracks, peekGenresCache, peekHomeFeedCache, searchTracks } from '@/services/catalog'
import { resolveUrl } from '@/services/http'
import { getDailyRecommendations, peekDailyCache } from '@/services/recommend'
import { playTrack, playQueue, usePlayer } from '@/services/player'
import { favoriteTracks, favoritesTotal } from '@/services/library'
import { loadSavedCollections, savedCollections, savedCollectionsTotal, savedCollectionsLoading, savedCollectionsError, savingCollectionIds, toggleSavedCollection } from '@/services/saved-collections'
import {
  createPlaylist,
  getPlaylists,
  loadFavorites,
} from '@/services/library'
import {
  loadMyLibrary,
  myLibraryItems,
  myLibraryLoading,
  removeFromLibrary,
} from '@/services/user-library'
import { currentUser, isAuthenticated } from '@/services/session'
import { useCapabilities } from '@/services/capabilities'
import { refreshUnreadCount, unreadCount } from '@/services/notifications'
import { formatTime } from '@/utils/format'
import { useI18n } from 'vue-i18n'
import type { ApiCollectionSummary, ApiPlaylistSummary, ApiUserLibraryItem, Track } from '@/types/music'

const { t } = useI18n()

// 当前激活的 tab 与固定 tab 列表（tab 切换核心状态；tab 值为切换逻辑用的中文字面量，展示层走 i18n）
const activeTab = ref('为你')
const tabs = ['为你', '发现', '曲库', '我的'] as const
const TAB_LABEL_KEYS: Record<string, string> = {
  '为你': 'homePage.tabForYou',
  '发现': 'homePage.tabDiscover',
  '曲库': 'homePage.tabLibrary',
  '我的': 'homePage.tabMine',
}
/** tab 展示文案：已知 tab 走 i18n，未知值原样兜底 */
function tabLabel(tab: string) {
  const key = TAB_LABEL_KEYS[tab]
  return key ? t(key) : tab
}

// —— 底部凸起黑胶：展示当前曲目封面，点击展开全屏播放器；顶栏铃铛：未读角标（99+ 封顶） ——
const { state: playerState } = usePlayer()
const unreadLabel = computed(() => (unreadCount.value > 99 ? '99+' : String(unreadCount.value)))


/** 点击顶栏铃铛：未登录先引导登录，已登录进消息中心 */
function onBellTap() {
  if (!isAuthenticated.value) {
    uni.navigateTo({ url: '/pages/auth/login' })
    return
  }
  goPage('/pages/notifications/index')
}

/** 通用页内跳转（「我的」页功能入口用） */
function goPage(url: string) {
  uni.navigateTo({ url })
}

// —— “为你”Feed 数据：精选曲目、最近加入、专题列表、头图专题、风格、错误态 ——
const collections = ref<readonly ApiCollectionSummary[]>([])
const genres = ref<readonly string[]>([])
const loadError = ref(false)

// —— “发现”搜索状态：关键词 / 结果 / 加载中 / 是否已发起过搜索 / 防抖计时器 ——
const query = ref('')
const searchResults = ref<Track[]>([])
const searchPending = ref(false)
const searched = ref(false)
let searchTimer: ReturnType<typeof setTimeout> | undefined

// —— “收藏 / 我的”页数据：我的歌单与加载态（收藏曲目来自 library 全局 favoriteTracks） ——
const myPlaylists = ref<readonly ApiPlaylistSummary[]>([])
const libraryLoading = ref(false)
const favoritesLoading = ref(false)
const favoritesError = ref(false)
const personalError = ref(false)
const onlyFavorites = ref(false)
const visibleFavorites = computed(() => {
  const items = favoriteTracks.value.filter(track => (publicLibraryGenre.value === '全部' || track.genres.includes(publicLibraryGenre.value)) && `${track.title} ${track.artist} ${track.album}`.toLowerCase().includes(query.value.trim().toLowerCase()))
  return publicLibrarySort.value === 'title' ? items.sort((a, b) => a.title.localeCompare(b.title)) : items
})
async function refreshFavorites() {
  if (!isAuthenticated.value) return
  favoritesLoading.value = true
  favoritesError.value = false
  try { await loadFavorites(true) } catch { favoritesError.value = true } finally { favoritesLoading.value = false }
}
async function refreshPersonal() {
  personalError.value = false
  try { await loadMyLibrary(true) } catch { personalError.value = true }
}
async function refreshSaved() { try { await loadSavedCollections(true) } catch { /* inline retry */ } }
async function unsaveCollection(item: ApiCollectionSummary) {
  try { await toggleSavedCollection(item) } catch { uni.showToast({ title: t('homePage.operationFailed'), icon: 'none' }) }
}
function selectFavorites() {
  if (!isAuthenticated.value) { goLogin(); return }
  onlyFavorites.value = !onlyFavorites.value
  if (onlyFavorites.value) void refreshFavorites()
}

// —— 新建歌单弹层（受控 Vue 弹层，替代 uni.showModal 的 editable 模式）——
// uni.showModal({editable:true}) 在 H5 首次关闭后无法再次打开（模态框 DOM 残留），
// 改用受控弹层，任意次数打开/关闭都可靠。
const createSheetVisible = ref(false)
const newPlaylistName = ref('')
const creatingPlaylist = ref(false)

// —— “曲库”页数据：我的曲库引用（UserLibraryItem，含私有与公开） ——
// 列表与加载态来自 services/user-library 的模块级单例（全局共享，移除后即时同步）。
// 移除进行中的曲目 Id（防重复点击 Modal 回调）。
const removingId = ref<string | null>(null)
// 是否开放用户上传本地音频（受能力开关控制）
const canUserUpload = computed(() => capabilities.capabilities.userUploads)

// 后端功能开关（用户歌单、歌曲需求、用户上传等能力是否开放）
const capabilities = useCapabilities()

// —— “曲库”页匿名态：公开曲库（GET /tracks 匿名可读，后端只返回已发布曲目） ——
// 未登录也能浏览并播放，收藏/歌单/评论等互动仍由各组件的 requireLogin() 拦截。
const publicLibraryItems = ref<Track[]>([])
const libraryScope = ref<'public' | 'personal'>('public')
function selectLibraryScope(scope: 'public' | 'personal') {
  libraryScope.value = scope
  if (scope === 'public') { if (!publicLibraryItems.value.length) void loadPublicLibrary(true) }
  else if (isAuthenticated.value) void refreshPersonal()
}
function browsePublic() { libraryScope.value = 'public'; query.value = ''; setTab('曲库') }
const publicLibraryPage = ref(1)
const publicLibraryTotal = ref(0)
const publicLibraryLoading = ref(false)
const publicLibraryFinished = ref(false)
const publicLibraryGenre = ref('全部')
const publicLibrarySort = ref<'recent' | 'title'>('recent')
// 请求序号：切风格/排序时丢弃过期响应，避免旧页数据覆盖新筛选结果
let publicLibrarySeq = 0
const filteredSearch = computed(() => searchResults.value.filter(track => publicLibraryGenre.value === '全部' || track.genres.includes(publicLibraryGenre.value)))

/**
 * 加载公开曲库（登录前后均可用，20 条/页追加）。
 * @param reset true 表示从第一页重新拉取（切换风格/排序）
 */
async function loadPublicLibrary(reset = false) {
  if (!reset && (publicLibraryLoading.value || publicLibraryFinished.value)) return
  const seq = ++publicLibrarySeq
  if (reset) {
    publicLibraryPage.value = 1
    publicLibraryFinished.value = false
  }
  publicLibraryError.value = false
  publicLibraryLoading.value = true
  try {
    const result = await getTracks({
      genre: publicLibraryGenre.value === '全部' ? undefined : publicLibraryGenre.value,
      sort: publicLibrarySort.value,
      page: publicLibraryPage.value,
      pageSize: 20,
    })
    if (seq !== publicLibrarySeq) return
    publicLibraryItems.value = reset
      ? [...result.items]
      : [...publicLibraryItems.value, ...result.items]
    publicLibraryTotal.value = result.total
    publicLibraryFinished.value =
      publicLibraryItems.value.length >= result.total || result.items.length === 0
    if (!publicLibraryFinished.value) publicLibraryPage.value += 1
  } catch {
    if (seq === publicLibrarySeq) {
      publicLibraryError.value = true
      uni.showToast({ title: t('homePage.publicLibraryLoadFailed'), icon: 'none' })
    }
  } finally {
    if (seq === publicLibrarySeq) publicLibraryLoading.value = false
  }
}

/** 切换公开曲库风格筛选 */
function selectPublicGenre(genre: string) {
  if (publicLibraryGenre.value === genre) return
  publicLibraryGenre.value = genre
  void loadPublicLibrary(true)
}

/** 切换公开曲库排序方式 */
function selectPublicSort(sort: 'recent' | 'title') {
  if (publicLibrarySort.value === sort) return
  publicLibrarySort.value = sort
  void loadPublicLibrary(true)
}

/** 公开曲目浏览触底时继续分页；搜索结果不触发分页。 */
function onScrollLower() {
  if (activeTab.value === '曲库' && libraryScope.value === 'public' && !onlyFavorites.value && !query.value.trim()) void loadPublicLibrary()
}

// 退出登录回到公开曲目；登录不改变已选公开视图。
watch(isAuthenticated, (authed) => {
  if (!authed) libraryScope.value = 'public'
  if (activeTab.value === '曲库') {
    if (libraryScope.value === 'public' && !publicLibraryItems.value.length) void loadPublicLibrary(true)
    else if (authed && libraryScope.value === 'personal') void refreshPersonal()
  }
})

/**
 * 把曲库条目映射为播放器所需的 Track 结构。
 * 私有曲目同样可被上传者本人播放（后端按 OwnerUserId 放行）。
 * @param item 曲库条目
 * @returns 播放器 Track
 */
function toLibraryTrack(item: ApiUserLibraryItem): Track {
  return {
    id: item.trackId,
    title: item.title,
    artist: item.artists.length ? item.artists.join(', ') : t('homePage.unknownArtist'),
    album: item.albumTitle ?? '',
    albumId: null,
    cover: item.artworkUrl ? resolveUrl(item.artworkUrl) : '',
    duration: Math.round((item.durationMs ?? 0) / 1000),
    audioUrl: resolveUrl(`/api/v1/tracks/${item.trackId}/stream`),
    genres: [],
  }
}

// 曲库全量播放队列（点击某曲目后以全部曲库为队列顺序播放）
const libraryQueue = computed(() => myLibraryItems.value.map(toLibraryTrack))

/**
 * 点击曲库行：以当前曲目为起点、全部曲库为队列开始播放。
 * @param index 点击的曲库条目在列表中的下标
 */
function handlePlayLibrary(index: number) {
  playTrack(libraryQueue.value[index], libraryQueue.value)
}

/**
 * 点击移除：二次确认后调用移除引用，服务层会即时从本地列表剔除。
 * 曲目本体保留（全局唯一），仅移除本人的引用。
 * @param item 目标曲库条目
 */
async function handleRemoveFromLibrary(item: ApiUserLibraryItem) {
  if (removingId.value) return
  uni.showModal({
    title: t('homePage.removeFromLibrary'),
    content: t('homePage.removeFromLibraryConfirm', { title: item.title }),
    confirmText: t('homePage.remove'),
    success: async ({ confirm }) => {
      if (!confirm) return
      removingId.value = item.trackId
      try {
        await removeFromLibrary(item.trackId)
        uni.showToast({ title: t('homePage.removed'), icon: 'none' })
      } catch {
        uni.showToast({ title: t('homePage.operationFailed'), icon: 'none' })
      } finally {
        removingId.value = null
      }
    },
  })
}

/** 跳转上传本地音频页（受 userUploads 能力开关控制） */
function goUpload() {
  uni.navigateTo({ url: '/pages/library/upload' })
}

// 头像抽屉显隐（QQ 式左侧滑出个人菜单）
const drawerVisible = ref(false)

/**
 * 点击头像打开通用设置抽屉；账号操作在执行时检查登录。
 */
function onAvatarTap() {
  drawerVisible.value = true
}

// 风格 chips：在服务端风格前补一个“全部”。后端空集合时回退到默认风格列表，避免空状态
// 仅剩“全部”而显得页面不完整
// chips 值同时用于 track.genres 过滤匹配，必须保留原中文；展示层经 GENRE_LABEL_KEYS 映射走 i18n
const GENRE_LABEL_KEYS: Record<string, string> = {
  '全部': 'home.genres',
  '华语': 'homePage.genreHuayu',
  '欧美': 'homePage.genreOumei',
  '日语': 'homePage.genreRiyu',
  '电子': 'homePage.genreDianzi',
  '民谣': 'homePage.genreMinyao',
  '摇滚': 'homePage.genreYaogun',
  '古风': 'homePage.genreGufeng',
}
/** 风格 chips 展示文案：已知风格走 i18n（如 R&B 无需翻译），未知风格原样兜底 */
function genreLabel(genre: string) {
  const key = GENRE_LABEL_KEYS[genre]
  return key ? t(key) : genre
}
const genreChips = computed(() => {
  const list = genres.value
  return ['全部', ...list]
})

const displayCollections = computed(() => collections.value.filter((item) => item.trackCount > 0))
// 头像首字母：取显示名首字符大写，缺省回退为 L
const avatarLetter = computed(() => currentUser.value?.displayName?.slice(0, 1).toUpperCase() || 'S')
const today = new Date().getDate()

/**
 * 加载“为你”首页 Feed（精选曲目/最近加入/专题）
 * 关键副作用：失败时置 loadError，页面展示曲库连接失败提示
 */
const homeLoading = ref(false)
async function loadHome() {
  homeLoading.value = true
  loadError.value = false
  try {
    const home = await getHomeFeed()
    collections.value = home.collections
  } catch {
    loadError.value = true
  } finally {
    homeLoading.value = false
  }
}

/**
 * 重新加载首页：精选大卡 / 精选专题 / 最近加入 / 每日推荐 / 风格 chips
 * 用于空状态卡的“重新加载”点击；并发拉取以缩短等待
 */
async function retryHome() {
  await Promise.allSettled([loadHome(), loadGenreChips(), loadDaily()])
}

// —— 每日推荐数据：为「今日灵感」提供主封面与堆叠预览 ——
const dailyTracks = ref<Track[]>([])
const dailyLoading = ref(false)
const dailyError = ref(false)
// 卡片右侧的 3 张迷你封面预览（堆叠进今日灵感）

// —— 「今日灵感」时间驱动个性化 ——
/** 当前时段（hour-based）：决定问候语、氛围标签、首选曲目风格（展示层走 i18n key，逻辑不依赖文案） */
const nowHour = new Date().getHours()
const timeGreeting = computed(() => {
  if (nowHour < 6) return { greetingKey: 'homePage.greetingNight', toneKey: 'homePage.toneNight' }
  if (nowHour < 11) return { greetingKey: 'homePage.greetingMorning', toneKey: 'homePage.toneMorning' }
  if (nowHour < 14) return { greetingKey: 'homePage.greetingNoon', toneKey: 'homePage.toneNoon' }
  if (nowHour < 18) return { greetingKey: 'homePage.greetingAfternoon', toneKey: 'homePage.toneAfternoon' }
  if (nowHour < 22) return { greetingKey: 'homePage.greetingEvening', toneKey: 'homePage.toneEvening' }
  return { greetingKey: 'homePage.greetingNight', toneKey: 'homePage.toneNight' }
})
/** 问候整句：含 {name} 插值，未登录或无昵称回退为「朋友」 */
const greetingText = computed(() =>
  t(timeGreeting.value.greetingKey, {
    name: isAuthenticated.value ? currentUser.value?.displayName ?? t('homePage.friend') : t('homePage.friend'),
  }),
)
const isHome = computed(() => activeTab.value === tabs[0])
const publicLibraryError = ref(false)

/**
 * 加载每日推荐（登录后为个性化结果，未登录为热度/最新兜底）。
 * 失败单独展示重试；空结果不渲染推荐卡。
 */
async function loadDaily() {
  dailyLoading.value = true
  dailyError.value = false
  try {
    const result = await getDailyRecommendations()
    dailyTracks.value = result.tracks
  } catch {
    dailyError.value = true
  } finally {
    dailyLoading.value = false
  }
}

/** 进入每日推荐列表页 */
function goDaily() {
  uni.navigateTo({ url: '/pages/recommend/daily' })
}


/**
 * 加载风格 chips
 * 后端没有风格标签时保持空集合（仅剩“全部”），页面展示“曲库中还没有风格标签。”
 */
const genresLoading = ref(false)
const genresError = ref(false)
async function loadGenreChips() {
  genresLoading.value = true
  genresError.value = false
  try {
    genres.value = await getGenres()
  } catch {
    genresError.value = true
  } finally {
    genresLoading.value = false
  }
}

/**
 * 监听搜索框输入：仅在“发现”tab 生效，280ms 防抖后调用搜索接口
 * 关键词清空时立即重置结果；每次输入先清掉上一个防抖定时器
 */
const searchError = ref(false)
let searchSeq = 0
function runSearch(value = query.value) {
  const seq = ++searchSeq
  if (searchTimer) clearTimeout(searchTimer)
  const keyword = value.trim()
  searchError.value = false
  searched.value = false
  if (!keyword) {
    searchResults.value = []
    searchPending.value = false
    return
  }
  searchPending.value = true
  searchTimer = setTimeout(async () => {
    try {
      const result = await searchTracks(keyword)
      if (seq !== searchSeq) return
      searchResults.value = result
      searched.value = true
    } catch {
      if (seq !== searchSeq) return
      searchError.value = true
    } finally {
      if (seq === searchSeq) searchPending.value = false
    }
  }, 280)
}
watch(query, (value) => runSearch(value))

/**
 * 切换底部 tab；切到“我的”时按需拉取我的歌单，切到“曲库”时按需拉取曲库引用
 * @param tab 目标 tab 名称
 */
function setTab(tab: string) {
  activeTab.value = tab
  if (tab === '我的') void loadLibrary()
  if (tab === '曲库') {
    if (libraryScope.value === 'personal' && isAuthenticated.value) void refreshPersonal()
    else if (!publicLibraryItems.value.length) void loadPublicLibrary(true)
  }
}



/**
 * 加载“我的”页数据（收藏列表 + 我的歌单）
 * 前置条件：已登录、未在加载中、后端开放了 userCollections 能力，否则直接跳过
 */
const libraryError = ref(false)
async function loadLibrary() {
  if (!isAuthenticated.value || libraryLoading.value) return
  void refreshFavorites()
  if (capabilities.capabilities.editorialCollections) void refreshSaved()
  if (!capabilities.capabilities.userCollections) return
  const userId = currentUser.value?.id
  libraryLoading.value = true
  libraryError.value = false
  try {
    const playlists = await getPlaylists()
    if (userId === currentUser.value?.id) myPlaylists.value = playlists.items
  } catch {
    libraryError.value = true
  } finally {
    libraryLoading.value = false
  }
}

// 每次页面重新显示（如从登录页/上传页返回）：若停留在“我的”/“曲库”tab 则刷新对应数据，
// 保证上传完成、登录状态变化后列表立即更新
onShow(() => {
  if (activeTab.value === '我的') void loadLibrary()
  if (activeTab.value === '曲库') {
    if (libraryScope.value === 'personal' && isAuthenticated.value) void refreshPersonal()
    else if (!publicLibraryItems.value.length) void loadPublicLibrary(true)
  }
  void refreshUnreadCount() // 从消息页返回/登录返回时刷新角标；未登录时内部静默失败
})

// 首次挂载：并行拉取首页 Feed 与风格；已登录时预加载收藏数据
onMounted(() => {
  // ============ 先用模块级缓存同步填充，避免 loading 闪烁 ============
  // 应用从后台切回前台时 Vue 组件重建、onMounted 再次触发。
  // 如果 service 层已有上次的缓存数据，立即同步赋值给 ref，
  // 页面就能在第一帧渲染已有内容，而不是闪一下 loading。
  // 填充完后仍然调 loadHome/loadGenreChips/loadDaily，
  // 它们内部会走 stale-while-revalidate 静默刷新。
  const cachedHome = peekHomeFeedCache()
  if (cachedHome) {
    collections.value = cachedHome.collections
  }
  const cachedGenres = peekGenresCache()
  if (cachedGenres) genres.value = cachedGenres
  const cachedDaily = peekDailyCache()
  if (cachedDaily) dailyTracks.value = cachedDaily.tracks

  // ============ 正常异步加载（缓存命中时静默刷新，未命中时发请求） ============
  void loadHome()
  void loadGenreChips()
  void loadDaily()
  if (isAuthenticated.value) void loadLibrary()
})
watch(() => currentUser.value?.id, () => {
  myPlaylists.value = []
  onlyFavorites.value = false
  dailyTracks.value = []
  void loadDaily()
  if (activeTab.value === '我的') void loadLibrary()
})


/**
 * 点击精选专题卡：真实合集进入详情，内置兜底合集（default-）跳对应风格浏览页
 * @param collection 专题摘要
 */
function tapCollection(collection: ApiCollectionSummary) {
  goCollection(collection)
}




/**
 * 进入精选专题详情
 * @param collection 专题摘要
 */
function goCollection(collection: ApiCollectionSummary) {
  uni.navigateTo({ url: `/pages/catalog/collection?id=${collection.id}` })
}



/** 进入歌曲需求池；功能开关关闭时 toast 拦截 */
function goRequests() {
  if (!capabilities.capabilities.songRequests) {
    uni.showToast({ title: t('homePage.requestsUnavailable'), icon: 'none' })
    return
  }
  uni.navigateTo({ url: '/pages/requests/index' })
}

/** 进入登录页（未登录的头像点击与收藏 tab 引导） */
function goLogin() {
  uni.navigateTo({ url: '/pages/auth/login' })
}

/**
 * 进入歌单详情
 * @param playlist 歌单摘要
 */
function goPlaylist(playlist: ApiPlaylistSummary) {
  uni.navigateTo({ url: `/pages/playlist/detail?id=${playlist.id}` })
}

/** 打开新建歌单弹层：复位输入框并显示受控弹层 */
function openCreatePlaylist() {
  newPlaylistName.value = ''
  createSheetVisible.value = true
}

/** 跳转歌单解析导入页（粘贴外部平台歌单链接生成新歌单） */
function goPlaylistImport() {
  uni.navigateTo({ url: '/pages/playlist/import' })
}

/** 关闭新建歌单弹层（取消/点击遮罩） */
function closeCreatePlaylist() {
  if (creatingPlaylist.value) return
  createSheetVisible.value = false
}

/** 确认新建歌单：校验名称非空 → 调用创建接口 → 刷新列表并关闭弹层 */
async function confirmCreatePlaylist() {
  const name = newPlaylistName.value.trim()
  // 表单校验：歌单名不能为空
  if (!name) {
    uni.showToast({ title: t('homePage.nameRequired'), icon: 'none' })
    return
  }
  if (creatingPlaylist.value) return
  creatingPlaylist.value = true
  try {
    await createPlaylist(name)
    uni.showToast({ title: t('homePage.playlistCreated'), icon: 'none' })
    newPlaylistName.value = ''
    createSheetVisible.value = false
    await loadLibrary()
  } catch {
    uni.showToast({ title: t('homePage.createFailed'), icon: 'none' })
  } finally {
    creatingPlaylist.value = false
  }
}

/**
 * 解析专题封面：有相对路径封面时拼接后端完整 URL，否则返回空串（由 image 兜底底色）
 * @param collection 专题摘要
 */
function coverOf(collection: ApiCollectionSummary) {
  return collection.artworkUrl ? resolveUrl(collection.artworkUrl) : ''
}
</script>

<template>
  <view class="app-shell">
    <!-- 背景彩色光晕（方向 B · 玻璃质感）：固定定位，位于内容之下 -->
    <view class="sm-glow-layer">
      <view class="sm-glow green" style="width: 260px; height: 260px; top: -90px; left: -90px;"></view>
      <view class="sm-glow blue" style="width: 220px; height: 220px; top: 310px; right: -90px;"></view>
      <view class="sm-glow purple" style="width: 200px; height: 200px; bottom: 70px; left: -70px;"></view>
    </view>

    <scroll-view class="page-scroll" scroll-y :show-scrollbar="false" @scrolltolower="onScrollLower">
      <!-- 顶部品牌栏：Logo + 消息铃铛（未读角标）+ 头像（点击滑出个人抽屉） -->
      <view class="topbar">
        <view class="brand-lockup">
          <text class="brand-name">shine<text class="brand-dot">.</text></text>
        </view>
        <view class="topbar-actions">
          <button class="bell-wrap" :aria-label="$t('nav.notifications')" @tap="onBellTap">
            <Bell :size="19" />
            <text v-if="unreadCount > 0" class="bell-badge">{{ unreadLabel }}</text>
          </button>
          <button class="avatar" :aria-label="$t('homePage.settings')" @tap="onAvatarTap">{{ avatarLetter }}</button>
        </view>
      </view>

      <view v-if="activeTab !== tabs[3]" class="page-heading">
        <text class="eyebrow">{{ $t(isHome ? 'homePage.recommendEyebrow' : activeTab === '发现' ? 'homePage.discoverEyebrow' : 'homePage.libraryEyebrow') }}</text>
        <text class="greeting">{{ $t(isHome ? 'homePage.recommendHeading' : activeTab === '发现' ? 'homePage.discoverHeading' : 'homePage.publicLibraryTitle') }}</text>
      </view>
      <view v-if="isHome" class="recommend-content">
        <view v-if="homeLoading && !displayCollections.length" class="inline-error" role="status">{{ $t('homePage.loading') }}</view>
        <button v-else-if="loadError" class="state-retry" @tap="retryHome">{{ $t('app.error') }} · {{ $t('app.retry') }}</button>
        <view v-else-if="!displayCollections.length" class="recommend-empty">{{ $t('homePage.collectionsEmpty') }}</view>
        <button v-if="displayCollections[0]" class="editorial-hero" @tap="tapCollection(displayCollections[0])">
          <view class="hero-art"><CoverArt :src="coverOf(displayCollections[0])" :seed="displayCollections[0].title" :rounded="12" :icon-size="72" /></view>
          <view class="hero-shade" />
          <text class="hero-badge">{{ $t('homePage.leadCollection') }}</text>
          <view class="hero-copy"><text class="hero-title">{{ displayCollections[0].title }}</text><text v-if="displayCollections[0].description" class="hero-description">{{ displayCollections[0].description }}</text><text class="hero-meta">{{ $t('homePage.trackCount', { count: displayCollections[0].trackCount }) }}</text></view>
          <view class="hero-arrow"><ChevronRight :size="22" /></view>
        </button>
        <view v-if="dailyLoading && !dailyTracks.length" class="inline-error" role="status">{{ $t('nav.recommendDaily') }} · {{ $t('homePage.loading') }}</view>
        <button v-else-if="dailyError" class="state-retry" @tap="loadDaily">{{ $t('nav.recommendDaily') }} · {{ $t('app.retry') }}</button>
        <button v-else-if="dailyTracks.length" class="daily-card" @tap="goDaily"><text class="calendar">{{ today }}</text><view class="daily-copy"><text class="daily-title">{{ $t('nav.recommendDaily') }}</text><text class="muted">{{ $t('homePage.dailyCaption') }} · {{ $t('homePage.trackCount', { count: dailyTracks.length }) }}</text></view><ChevronRight :size="18" /></button>
        <view v-else class="inline-error">{{ $t('homePage.dailyEmpty') }}</view>
        <view class="section-heading"><text class="section-title">{{ $t('homePage.collectionShelf') }}</text><button class="text-action" @tap="browsePublic">{{ $t('homePage.tabLibrary') }} ↗</button></view>
        <view v-if="displayCollections.length > 1" class="collection-shelf">
          <button v-for="collection in displayCollections.slice(1)" :key="collection.id" class="shelf-card" @tap="tapCollection(collection)"><view class="shelf-cover"><CoverArt :src="coverOf(collection)" :seed="collection.title" :rounded="8" :icon-size="32" /></view><text class="shelf-title">{{ collection.title }}</text><text class="muted">{{ $t('homePage.trackCount', { count: collection.trackCount }) }}</text></button>
        </view>
        <view v-else class="inline-error">{{ $t('homePage.moreCollectionsEmpty') }}</view>
      </view>
      <ChartBrowser v-else-if="activeTab === '发现'" />

      <!-- 曲库：匿名态为公开曲库（可听歌），登录后为我的上传/引用曲目（含私有与公开） -->
      <template v-else-if="activeTab === '曲库'">
        <view class="library-wrap">
          <view class="library-segments">
            <button :class="{ selected: libraryScope === 'public' }" :aria-pressed="libraryScope === 'public'" @tap="selectLibraryScope('public')">{{ $t('homePage.publicTab') }}</button>
            <button :class="{ selected: libraryScope === 'personal' }" :aria-pressed="libraryScope === 'personal'" @tap="selectLibraryScope('personal')">{{ $t('homePage.personalTab') }}</button>
          </view>
          <!-- 未登录：温和登录提示 + 公开曲库列表（互动操作由各组件的 requireLogin 拦截） -->
          <template v-if="libraryScope === 'public'">
            <view v-if="!isAuthenticated" class="public-hint">
              <text class="public-hint-title">{{ $t('homePage.publicLibraryHint') }}</text>
              <button class="public-hint-action" @tap="goLogin">
                <text>{{ $t('homePage.publicLibraryLoginAction') }}</text>
              </button>
            </view>
            <view class="search-box"><Search :size="19" /><input v-model="query" confirm-type="search" :placeholder="$t('homePage.searchPlaceholder')" :aria-label="$t('homePage.searchPlaceholder')" /></view>
            <button class="favorite-filter" :class="{ selected: onlyFavorites }" :aria-pressed="onlyFavorites" @tap="selectFavorites"><Heart :size="16" />{{ $t('homePage.favoritesFilter') }}</button>
            <view v-if="genresLoading" class="muted" role="status">{{ $t('homePage.loading') }}</view>
            <button v-else-if="genresError" class="state-retry" @tap="loadGenreChips">{{ $t('homePage.genreLoadFailed') }} · {{ $t('app.retry') }}</button>
            <scroll-view class="public-chips" scroll-x :show-scrollbar="false"><view class="public-chips-row"><button v-for="genre in genreChips" :key="genre" class="public-chip" :class="{ on: publicLibraryGenre === genre }" :aria-pressed="publicLibraryGenre === genre" @tap="selectPublicGenre(genre)">{{ genreLabel(genre) }}</button></view></scroll-view>
            <template v-if="onlyFavorites">
              <view v-if="favoritesLoading" class="inline-error" role="status">{{ $t('homePage.loading') }}</view>
              <button v-else-if="favoritesError" class="state-retry" @tap="refreshFavorites">{{ $t('app.error') }} · {{ $t('app.retry') }}</button>
              <view v-else-if="!visibleFavorites.length" class="inline-error">{{ $t('homePage.noMatch') }}</view>
              <view v-else><TrackRow v-for="track in visibleFavorites" :key="track.id" :track="track" :queue="visibleFavorites" /></view>
            </template>
            <view v-else-if="query.trim()" class="search-results">
              <text class="section-title">{{ $t('homePage.searchResultsFor', { query: query.trim() }) }}</text>
              <view v-if="searchPending" class="inline-error">{{ $t('homePage.searching') }}</view>
              <button v-else-if="searchError" class="state-retry" @tap="runSearch()">{{ $t('app.error') }} · {{ $t('app.retry') }}</button>
              <view v-else-if="searched && !filteredSearch.length" class="no-results"><text>{{ $t('homePage.noMatch') }}</text><button v-if="isAuthenticated && capabilities.capabilities.songRequests" class="state-retry" @tap="goRequests">{{ $t('homePage.tellAdmins') }}</button></view>
              <view v-else class="track-list"><TrackRow v-for="track in filteredSearch" :key="track.id" :track="track" :queue="filteredSearch" /></view>
            </view>
            <template v-else>

            <view class="section-heading public-heading">
              <view>
                <text class="section-title">{{ $t(publicLibrarySort === 'recent' ? 'homePage.recentlyAdded' : 'homePage.publicLibraryTitle') }}</text>
              </view>
              <text class="library-count">{{ $t('homePage.publicLibraryTotal', { count: publicLibraryTotal }) }}</text>
            </view>

            <text v-if="!genresLoading && !genresError && !genres.length" class="muted">{{ $t('homePage.genresEmpty') }}</text>

            <!-- 排序切换 -->
            <view class="public-sort">
              <button
                class="public-sort-item"
                :class="{ on: publicLibrarySort === 'recent' }"
                @tap="selectPublicSort('recent')"
              >
                <text>{{ $t('homePage.publicLibrarySortRecent') }}</text>
              </button>
              <button
                class="public-sort-item"
                :class="{ on: publicLibrarySort === 'title' }"
                @tap="selectPublicSort('title')"
              >
                <text>{{ $t('homePage.publicLibrarySortTitle') }}</text>
              </button>
            </view>
            <button v-if="publicLibraryItems.length" class="play-list" :disabled="publicLibraryLoading" @tap="playQueue(publicLibraryItems)"><Play :size="16" />{{ $t('catalogPage.playAll') }}</button>

            <view v-if="publicLibraryLoading && !publicLibraryItems.length" class="inline-error">{{ $t('homePage.loading') }}</view>
            <button v-else-if="publicLibraryError" class="state-retry" @tap="loadPublicLibrary(true)">{{ $t('homePage.publicLibraryLoadFailed') }} · {{ $t('app.retry') }}</button>
            <view v-else-if="!publicLibraryItems.length" class="empty-state">
              <view class="empty-icon"><Library :size="23" /></view>
              <text class="empty-title">{{ $t('homePage.publicLibraryEmpty') }}</text>
            </view>
            <template v-else>
              <view class="track-list">
                <TrackRow
                  v-for="(track, index) in publicLibraryItems"
                  :key="track.id"
                  :track="track"
                  :index="index"
                  :queue="publicLibraryItems"
                />
              </view>
              <view v-if="publicLibraryLoading" class="public-more">{{ $t('homePage.loading') }}</view>
              <view v-else-if="publicLibraryFinished" class="public-more">{{ $t('homePage.publicLibraryFinished') }}</view>
              <button v-else class="state-retry" @tap="loadPublicLibrary()">{{ $t('homePage.more') }}</button>
            </template>
            </template>
          </template>

          <!-- 已登录：标题行（上传入口受开关控制）/ 加载 / 空态 / 列表 -->
          <view v-else-if="!isAuthenticated" class="empty-state"><text class="empty-title">{{ $t('homePage.libraryLoginTitle') }}</text><text class="empty-copy">{{ $t('homePage.libraryLoginCopy') }}</text><button class="state-retry" @tap="goLogin">{{ $t('homePage.loginOrRegister') }}</button></view>
          <template v-else>
            <view class="section-heading">
              <view>
                <text class="section-title">{{ $t('homePage.myLibraryTitle') }}</text>
              </view>
              <view v-if="canUserUpload" class="create-playlist" @tap="goUpload">
                <Upload :size="17" /><text>{{ $t('homePage.upload') }}</text>
              </view>
              <text v-else class="library-count">{{ $t('homePage.trackCount', { count: myLibraryItems.length }) }}</text>
            </view>

            <view v-if="myLibraryLoading && !myLibraryItems.length" class="inline-error">{{ $t('homePage.loading') }}</view>
            <button v-else-if="personalError" class="state-retry" @tap="refreshPersonal">{{ $t('app.error') }} · {{ $t('app.retry') }}</button>
            <view v-else-if="!myLibraryItems.length" class="empty-state">
              <view class="empty-icon"><Library :size="23" /></view>
              <text class="empty-title">{{ $t('homePage.libraryEmptyTitle') }}</text>
              <text class="empty-copy">{{ $t('homePage.libraryEmptyCopy') }}</text>
              <view v-if="canUserUpload" class="empty-upload" @tap="goUpload">
                <Upload :size="16" /><text>{{ $t('nav.libraryUpload') }}</text>
              </view>
            </view>

            <template v-else>
              <view class="lib-head"><text class="lib-count">{{ $t('homePage.totalTracks', { count: myLibraryItems.length }) }}</text></view>
              <view
                v-for="(item, index) in myLibraryItems"
                :key="item.trackId"
                class="lib-row"
                :class="{ 'is-removing': removingId === item.trackId }"
                @tap="handlePlayLibrary(index)"
              >
                <view class="lib-cover">
                  <CoverArt :src="item.artworkUrl ? resolveUrl(item.artworkUrl) : ''" :rounded="14" :icon-size="16" />
                </view>
                <view class="lib-copy">
                  <view class="lib-title-row">
                    <text class="lib-title">{{ item.title }}</text>
                    <text v-if="!item.isPublic" class="lib-badge private">{{ $t('homePage.privateBadge') }}</text>
                    <text v-else class="lib-badge public">{{ $t('homePage.publicBadge') }}</text>
                  </view>
                  <text class="lib-artist">{{ item.artists.join(', ') || $t('homePage.unknownArtist') }}</text>
                </view>
                <view class="lib-meta">
                  <text class="lib-duration">{{ formatTime(Math.round((item.durationMs ?? 0) / 1000)) }}</text>
                  <view class="lib-remove" @tap.stop="handleRemoveFromLibrary(item)">
                    <Trash2 :size="17" />
                  </view>
                </view>
              </view>
            </template>
          </template>
        </view>
      </template>

      <!-- 我的（内容为中心的个人页：资料 / 快捷入口 / 歌单宫格 / 喜欢的音乐横幅） -->
      <template v-else>
        <view class="library-wrap">
          <template v-if="isAuthenticated">

            <button class="profile-card" @tap="goPage('/pages/profile/index')"><text class="profile-avatar">{{ avatarLetter }}</text><view class="profile-copy"><text class="profile-name">{{ currentUser?.displayName }}</text><text class="profile-email">{{ $t('homePage.profileCaption') }}</text></view><ChevronRight :size="20" /></button>
            <button class="favorites-banner" @tap="goPage('/pages/library/favorites')"><view class="favorite-icon"><Heart :size="26" fill="currentColor" /></view><view class="favorites-copy"><text class="favorites-name">{{ $t('homePage.favoriteMusic') }}</text><text class="favorites-meta">{{ favoritesLoading ? $t('homePage.loading') : favoritesError ? $t('app.error') : $t('homePage.trackCount', { count: favoritesTotal }) }}</text></view><ChevronRight :size="18" /></button>
            <button v-if="favoritesError" class="state-retry" @tap="refreshFavorites">{{ $t('app.retry') }}</button>
            <view class="quick-circles">
              <button class="quick-circle" @tap="goPage('/pages/history/index')"><History :size="18" /><text>{{ $t('nav.playHistory') }}</text></button>
              <button v-if="canUserUpload" class="quick-circle" @tap="goUpload"><Upload :size="18" /><text>{{ $t('homePage.upload') }}</text></button>
              <button class="quick-circle" @tap="onAvatarTap"><UserRound :size="18" /><text>{{ $t('homePage.settings') }}</text></button>
            </view>

            <!-- 我的歌单：2 列封面宫格（宽视口 3 列） -->
            <view v-if="capabilities.capabilities.userCollections" class="library-block">
              <view class="section-heading">
                <view><text class="section-title">{{ $t('homePage.myPlaylists') }}</text></view>
                <view class="playlist-heading-actions">
                  <button class="create-playlist" @tap="goPlaylistImport"><Download :size="17" /><text>{{ $t('homePage.import') }}</text></button>
                  <button class="create-playlist" @tap="openCreatePlaylist"><Plus :size="17" /><text>{{ $t('homePage.create') }}</text></button>
                </view>
              </view>
              <view v-if="libraryLoading" class="inline-error">{{ $t('homePage.loading') }}</view>
              <view v-else-if="libraryError" class="inline-error"><button class="state-retry" @tap="loadLibrary">{{ $t('homePage.feedErrorTitle') }} · {{ $t('app.retry') }}</button></view>
              <view v-else-if="!myPlaylists.length" class="inline-error">{{ $t('homePage.noPlaylists') }}</view>
              <view v-else class="playlist-grid">
                <button
                  v-for="playlist in myPlaylists"
                  :key="playlist.id"
                  class="playlist-tile"
                  @tap="goPlaylist(playlist)"
                >
                  <view class="playlist-tile-cover">
                    <CoverArt :src="playlist.artworkUrl ? resolveUrl(playlist.artworkUrl) : ''" :seed="playlist.name" :rounded="8" :icon-size="26" />
                  </view>
                  <text class="playlist-tile-name">{{ playlist.name }}</text>
                  <text class="playlist-tile-meta">{{ $t('homePage.trackCount', { count: playlist.trackCount }) }}</text>
                </button>
              </view>
            </view>
            <view v-else class="inline-error">{{ $t('homePage.collectionsUnavailable') }}</view>
            <view v-if="capabilities.capabilities.editorialCollections" class="library-block">
              <view class="section-heading"><text class="section-title">{{ $t('homePage.savedCollections') }}</text><text v-if="!savedCollectionsLoading && !savedCollectionsError" class="muted">{{ savedCollectionsTotal }}</text></view>
              <view v-if="savedCollectionsLoading" class="inline-error" role="status">{{ $t('homePage.loading') }}</view>
              <button v-else-if="savedCollectionsError" class="state-retry" @tap="refreshSaved">{{ $t('app.error') }} · {{ $t('app.retry') }}</button>
              <view v-else-if="!savedCollections.length" class="inline-error">{{ $t('homePage.savedEmpty') }}</view>
              <view v-for="collection in savedCollections" :key="collection.id" class="saved-row"><button class="saved-link" @tap="goCollection(collection)"><view class="saved-cover"><CoverArt :src="coverOf(collection)" :seed="collection.title" :rounded="8" :icon-size="24" /></view><view class="saved-copy"><text class="shelf-title">{{ collection.title }}</text><text class="muted">{{ $t('homePage.trackCount', { count: collection.trackCount }) }} · {{ $t('catalogPage.editorialPicks') }}</text></view><ChevronRight :size="16" /></button><button class="unsave" :disabled="savingCollectionIds.size > 0 || savedCollectionsLoading" :aria-label="$t('homePage.unsaveCollection') + ' ' + collection.title" @tap="unsaveCollection(collection)"><Heart :size="18" fill="currentColor" /></button></view>
            </view>

          </template>

          <!-- 未登录：登录引导卡（本地缓存的喜欢的音乐仍可展示） -->
          <template v-else>
            <view class="login-card">
              <view class="login-icon"><AudioLines :size="22" /></view>
              <text class="login-title">{{ $t('homePage.loginTitle') }}</text>
              <text class="login-copy">{{ $t('homePage.loginSyncCopy') }}</text>
              <button class="login-button" @tap="goLogin"><text>{{ $t('homePage.loginOrRegister') }}</text></button>
            </view>
          </template>
        </view>
      </template>

      <view class="bottom-spacer" :class="{ 'has-player': playerState.currentTrack }"></view>
    </scroll-view>

    <!-- 全局迷你播放器（有当前曲目时内部自行显示） -->
    <PlayerBar :with-tabbar="true" />

    <!-- 头像抽屉：点击头像滑出的个人菜单（QQ 式） -->
    <ProfileDrawer v-model="drawerVisible" />

    <!-- 底部自定义 tabbar：为你 / 发现 / [正在播放黑胶凸起] / 曲库 / 我的 -->
    <view class="tabbar">
      <button v-for="tab in tabs" :key="tab" class="tab-item" :class="{ active: activeTab === tab }" :aria-current="activeTab === tab ? 'page' : undefined" @tap="setTab(tab)">
        <Sparkles v-if="tab === '为你'" :size="20" />
        <Compass v-else-if="tab === tabs[1]" :size="20" />
        <Library v-else-if="tab === '曲库'" :size="20" />
        <UserRound v-else :size="20" />
        <text>{{ tabLabel(tab) }}</text>
      </button>
    </view>

    <!-- 新建歌单弹层（受控 Dialog，替代 uni.showModal editable） -->
    <view v-if="createSheetVisible" class="sheet-mask" @tap="closeCreatePlaylist">
      <view class="sheet-dialog" @tap.stop>
        <text class="sheet-title">{{ $t('homePage.createPlaylist') }}</text>
        <input
          v-model="newPlaylistName"
          class="sheet-input"
          type="text"
          maxlength="60"
          :placeholder="$t('homePage.playlistNamePlaceholder')"
          :focus="true"
          confirm-type="done"
          @confirm="confirmCreatePlaylist"
        />
        <view class="sheet-actions">
          <button class="sheet-btn ghost" :disabled="creatingPlaylist" @tap="closeCreatePlaylist">{{ $t('homePage.cancel') }}</button>
          <button class="sheet-btn primary" :disabled="creatingPlaylist || !newPlaylistName.trim()" @tap="confirmCreatePlaylist">
            {{ creatingPlaylist ? $t('homePage.creating') : $t('homePage.createBtn') }}
          </button>
        </view>
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
.app-shell { position: relative; width: 100%; min-height: 100vh; background: var(--sm-page-bg); color: var(--sm-text); }
.page-scroll { position: relative; height: 100vh; height: 100dvh; }
button { margin: 0; padding: 0; font: inherit; line-height: 1.5; background: transparent; color: inherit; text-align: left; border-radius: 8px; }
button::after { border: 0; }
button:hover { opacity: .88; }
button:focus-visible, input:focus-visible { outline: 2px solid var(--sm-accent); outline-offset: 3px; }
.topbar { display: flex; justify-content: space-between; align-items: center; padding: calc(env(safe-area-inset-top) + 20px) 24px 0; }
.brand-name { font-size: 24px; font-weight: 850; letter-spacing: -1px; }
.brand-dot { color: var(--sm-accent); }
.topbar-actions { display: flex; align-items: center; gap: 12px; }
.avatar, .bell-wrap { position: relative; display: grid; place-items: center; width: 36px; height: 36px; border-radius: 50%; }
.avatar { background: var(--sm-avatar-bg); color: var(--sm-cover-ink); font-size: 13px; font-weight: 700; }
.bell-badge { position: absolute; top: -4px; right: -4px; padding: 0 4px; border-radius: 12px; font-size: 10px; background: var(--sm-danger); color: var(--sm-accent-ink); }
.page-heading { display: flex; flex-direction: column; align-items: flex-start; padding: 28px 24px 24px; gap: 8px; }
.eyebrow { color: var(--sm-accent); font-size: 11px; letter-spacing: 2px; font-weight: 700; }
.greeting { font-size: clamp(28px, 5vw, 36px); font-weight: 800; letter-spacing: -1px; line-height: 1.25; }
.recommend-content, .library-wrap { padding: 0 24px; }
.library-wrap { padding-top: 8px; }
.editorial-hero { position: relative; isolation: isolate; display: flex; align-items: flex-end; min-height: 290px; width: 100%; padding: 80px 24px 24px; box-sizing: border-box; border-radius: 12px; background: var(--sm-hero-bg); color: #fff; }
.hero-art, .hero-shade { position: absolute; inset: 0; z-index: -1; border-radius: inherit; }
.hero-shade { background: var(--sm-hero-shade); }
.hero-badge { position: absolute; top: 24px; left: 24px; border: 1px solid #ffffff70; padding: 4px 8px; font-size: 10px; letter-spacing: 2px; }
.hero-copy { min-width: 0; flex: 1; padding-right: 16px; }
.hero-title { display: block; font-size: clamp(28px, 5vw, 36px); font-weight: 800; line-height: 1.25; overflow-wrap: anywhere; }
.hero-description { display: block; margin-top: 12px; font-size: 12px; line-height: 1.6; }
.hero-meta { display: block; margin-top: 12px; font-size: 11px; opacity: .85; }
.hero-arrow { display: grid; place-items: center; flex: none; width: 44px; height: 44px; border-radius: 50%; background: #faf9f5; color: #252521; }
.daily-card { display: flex; width: 100%; gap: 16px; align-items: center; padding: 20px 0; border-bottom: 1px solid var(--sm-divider); border-radius: 0; }
.calendar { display: grid; place-items: center; flex: none; width: 48px; height: 52px; background: var(--sm-accent-soft); color: var(--sm-accent); border-radius: 8px; font: 28px Georgia, serif; }
.daily-copy { flex: 1; min-width: 0; }
.daily-title { display: block; font-size: 14px; font-weight: 700; margin-bottom: 4px; }
.muted { display: block; color: var(--sm-text-2); font-size: 11px; line-height: 1.6; }
.section-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; margin: 28px 0 16px; }
.section-title { font-size: 19px; font-weight: 750; }
.text-action { font-size: 12px; color: var(--sm-text-2); }
.collection-shelf { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
.shelf-card { min-width: 0; width: 100%; align-self: start; }
.shelf-cover { width: 100%; aspect-ratio: 1; }
.shelf-title { display: block; margin: 8px 0 4px; font-size: 12px; font-weight: 650; overflow-wrap: anywhere; }
.library-segments { display: flex; gap: 8px; margin-bottom: 16px; }
.library-segments button { padding: 8px 16px; font-size: 12px; color: var(--sm-text-2); border-radius: 24px; }
.library-segments .selected, .favorite-filter.selected { color: var(--sm-accent); background: var(--sm-accent-soft); }
.public-hint { display: flex; align-items: center; gap: 12px; padding: 8px 0; font-size: 11px; color: var(--sm-text-2); }
.public-hint-title { flex: 1; }
.public-hint-action { color: var(--sm-accent); }
.search-box { display: flex; align-items: center; gap: 12px; padding: 12px 16px; margin-top: 12px; border-radius: 8px; background: var(--sm-surface); color: var(--sm-text-2); }
.search-box input { flex: 1; min-width: 0; font-size: 13px; color: var(--sm-text); }
.favorite-filter { display: flex; align-items: center; gap: 8px; padding: 8px 16px; border-radius: 24px; margin-top: 16px; background: var(--sm-surface); color: var(--sm-text-2); font-size: 12px; }
.public-chips { width: 100%; margin: 16px 0; }
.public-chips-row { display: flex; gap: 8px; padding: 4px; }
.public-chip { flex: none; padding: 8px 16px; border-radius: 24px; white-space: nowrap; background: var(--sm-surface); color: var(--sm-text-2); font-size: 12px; }
.public-chip.on { background: var(--sm-text); color: var(--sm-bg); }
.public-sort { display: flex; gap: 16px; flex-wrap: wrap; margin: 16px 0; }
.public-sort-item { padding: 8px 0; font-size: 12px; color: var(--sm-text-2); }
.public-sort-item.on { color: var(--sm-text); font-weight: 700; }
.play-list { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; font-size: 13px; padding: 8px 0; }
.public-more, .library-count, .lib-count { font-size: 12px; color: var(--sm-text-2); padding: 12px 0; }
.inline-error, .no-results, .recommend-empty, .empty-state { padding: 24px 0; color: var(--sm-text-2); font-size: 13px; line-height: 1.7; }
.state-retry { padding: 12px 16px; margin: 12px 0; background: var(--sm-surface); color: var(--sm-accent); font-size: 13px; }
.empty-state { display: flex; flex-direction: column; align-items: center; gap: 12px; }
.empty-title { color: var(--sm-text); font-weight: 650; }
.profile-card { display: flex; align-items: center; gap: 16px; width: 100%; margin: 24px 0; }
.profile-avatar { display: grid; place-items: center; width: 64px; height: 64px; flex: none; border-radius: 50%; background: var(--sm-avatar-bg); color: var(--sm-cover-ink); font-size: 24px; }
.profile-copy { min-width: 0; flex: 1; }
.profile-name { display: block; font-size: 24px; font-weight: 750; overflow-wrap: anywhere; }
.profile-email { display: block; margin-top: 4px; color: var(--sm-text-2); font-size: 11px; }
.favorites-banner { display: flex; align-items: center; gap: 16px; width: 100%; padding: 20px; box-sizing: border-box; background: var(--sm-accent-soft); border-radius: 12px; }
.favorite-icon { display: grid; place-items: center; width: 52px; height: 52px; flex: none; border-radius: 8px; background: var(--sm-accent); color: var(--sm-accent-ink); transform: rotate(-5deg); }
.favorites-copy { flex: 1; min-width: 0; }
.favorites-name { display: block; font-size: 17px; font-weight: 700; }
.favorites-meta { display: block; font-size: 11px; margin-top: 6px; color: var(--sm-text-2); }
.quick-circles { display: flex; justify-content: space-around; flex-wrap: wrap; gap: 8px; padding: 20px 0; border-bottom: 1px solid var(--sm-divider); }
.quick-circle { display: flex; align-items: center; gap: 8px; font-size: 12px; padding: 8px 0; }
.playlist-heading-actions { display: flex; gap: 16px; }
.create-playlist { display: flex; align-items: center; gap: 4px; font-size: 12px; color: var(--sm-text-2); }
.playlist-grid { display: flex; flex-direction: column; gap: 8px; }
.playlist-tile { display: grid; grid-template-columns: 64px minmax(0, 1fr); column-gap: 16px; align-items: center; width: 100%; padding: 8px 0; }
.playlist-tile-cover { width: 64px; height: 64px; grid-row: span 2; }
.playlist-tile-name { font-size: 13px; font-weight: 650; align-self: end; overflow-wrap: anywhere; }
.playlist-tile-meta { font-size: 11px; color: var(--sm-text-2); align-self: start; margin-top: 4px; }
.saved-row { display: flex; align-items: center; gap: 8px; }
.saved-link { display: flex; align-items: center; gap: 16px; flex: 1; min-width: 0; padding: 8px 0; }
.saved-cover { width: 64px; height: 64px; flex: none; }
.saved-copy { flex: 1; min-width: 0; }
.unsave { display: grid; place-items: center; width: 40px; height: 40px; flex: none; color: var(--sm-accent); }
.login-card { display: flex; flex-direction: column; gap: 16px; padding: 32px 0; }
.login-title { font-size: 24px; font-weight: 750; }
.login-copy { font-size: 13px; color: var(--sm-text-2); }
.login-button { background: var(--sm-accent); color: var(--sm-accent-ink); border-radius: 24px; padding: 12px 20px; font-size: 14px; text-align: center; }
.lib-row { display: flex; align-items: center; gap: 12px; padding: 12px 0; border-bottom: 1px solid var(--sm-divider); }
.lib-cover { width: 48px; height: 48px; flex: none; }
.lib-copy { flex: 1; min-width: 0; }
.lib-title-row { display: flex; align-items: center; gap: 8px; }
.lib-title { font-size: 13px; overflow-wrap: anywhere; }
.lib-badge, .lib-artist, .lib-duration { font-size: 10px; color: var(--sm-text-2); }
.lib-badge { flex: none; padding: 2px 4px; background: var(--sm-surface); border-radius: 4px; }
.lib-meta { display: flex; align-items: center; gap: 8px; }
.lib-remove { padding: 8px; color: var(--sm-text-2); }
.is-removing { opacity: .5; pointer-events: none; }
.tabbar { position: fixed; z-index: 3; bottom: 0; left: 0; right: 0; display: flex; align-items: center; height: 56px; padding: 0 16px env(safe-area-inset-bottom); background: var(--sm-tabbar-bg); backdrop-filter: blur(16px); }
.tab-item { position: relative; display: flex; align-items: center; flex-direction: column; gap: 4px; flex: 1; min-width: 0; font-size: 10px; color: var(--sm-text-3); padding: 8px; }
.tab-item.active { color: var(--sm-accent); font-weight: 700; }
.tab-item.active::before { content: ''; position: absolute; bottom: 2px; width: 3px; height: 3px; background: currentColor; border-radius: 50%; }
.bottom-spacer { height: calc(80px + env(safe-area-inset-bottom)); }
.bottom-spacer.has-player { height: calc(156px + env(safe-area-inset-bottom)); }
.sheet-mask { position: fixed; inset: 0; z-index: 60; display: flex; align-items: center; justify-content: center; padding: 24px; background: var(--sm-mask); }
.sheet-dialog { width: 100%; max-width: 440px; max-height: 80vh; overflow-y: auto; padding: 24px; border-radius: 12px; background: var(--sm-bg-soft); }
.sheet-title { display: block; font-size: 20px; font-weight: 750; }
.sheet-input { box-sizing: border-box; height: 48px; margin-top: 20px; padding: 0 12px; background: var(--sm-surface); border: 1px solid var(--sm-border); border-radius: 8px; font-size: 14px; }
.sheet-actions { display: flex; gap: 12px; margin-top: 24px; }
.sheet-btn { flex: 1; padding: 12px; text-align: center; font-size: 14px; }
.sheet-btn.ghost { background: var(--sm-surface); }
.sheet-btn.primary { background: var(--sm-accent); color: var(--sm-accent-ink); }
@media (min-width: 800px) { .app-shell { max-width: 940px; margin: auto; } .topbar { padding-left: 32px; padding-right: 32px; } .page-heading { padding: 28px 32px 24px; } .recommend-content, .library-wrap { padding-left: 32px; padding-right: 32px; } .editorial-hero { min-height: 340px; } .collection-shelf { gap: 20px; } .tabbar { left: max(0px, calc((100vw - 940px) / 2)); right: max(0px, calc((100vw - 940px) / 2)); } }
@media (max-width: 360px) { .topbar, .page-heading, .recommend-content, .library-wrap { padding-left: 16px; padding-right: 16px; } .collection-shelf { gap: 8px; } .hero-title { font-size: 28px; } }
@media (prefers-reduced-motion: no-preference) { .collection-shelf, .playlist-grid { animation: enter 260ms cubic-bezier(.2,.85,.25,1.05) both; } @keyframes enter { from { opacity: .4; transform: translateY(6px); } to { opacity: 1; transform: none; } } }
</style>
