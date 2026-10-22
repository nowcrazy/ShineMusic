<script setup lang="ts">
/**
 * 首页：应用主框架页，内含“为你 / 发现 / 曲库 / 我的”四个自定义 tab（非 tabBar 页面，底部为自绘 tabbar，
 * 中间为凸起的「消息」按钮，带未读角标，点击进入消息中心）。
 * 进入参数：无 onLoad query。
 * - 为你：首页 Feed（精选专题大卡、专题横滑、风格筛选、最近加入曲目）；
 * - 发现：风格浏览 + 关键词搜索（防抖）；
 * - 曲库：我的曲库（上传/引用曲目，含私有与公开），支持播放、移除引用、上传入口；
 * - 我的：登录后展示个人资料卡、快捷设置（主题/管理入口）、我的歌单、喜欢的音乐，未登录展示登录引导。
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
  Heart,
  History,
  Inbox,
  Library,
  ListMusic,
  Music2,
  Palette,
  Play,
  Plus,
  Search,
  Sparkles,
  Trash2,
  Upload,
  User,
  UserCog,
  UserRound,
} from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import CoverArt from '@/components/CoverArt.vue'
import ProfileDrawer from '@/components/ProfileDrawer.vue'
import QuickPanel from '@/components/QuickPanel.vue'
import { getGenres, getHomeFeed, searchTracks } from '@/services/catalog'
import { resolveUrl } from '@/services/http'
import { getDailyRecommendations } from '@/services/recommend'
import { playQueue, playTrack } from '@/services/player'
import {
  createPlaylist,
  favoriteTracks,
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
import type { ApiCollectionSummary, ApiPlaylistSummary, ApiUserLibraryItem, Track } from '@/types/music'

// 当前激活的 tab 与固定 tab 列表（tab 切换核心状态）
const activeTab = ref('为你')
const tabs = ['为你', '发现', '曲库', '我的'] as const

// —— 底部凸起按钮：未读角标（99+ 封顶）；已登录点击弹出快捷面板，未登录先引导登录 ——
const unreadLabel = computed(() => (unreadCount.value > 99 ? '99+' : String(unreadCount.value)))
const panelVisible = ref(false)

function onFabTap() {
  if (!isAuthenticated.value) {
    uni.navigateTo({ url: '/pages/auth/login' })
    return
  }
  panelVisible.value = true
}

/** 通用页内跳转（「我的」页功能入口用） */
function goPage(url: string) {
  uni.navigateTo({ url })
}

// —— “为你”Feed 数据：精选曲目、最近加入、专题列表、头图专题、风格、错误态 ——
const featured = ref<Track | null>(null)
const recentTracks = ref<Track[]>([])
const collections = ref<readonly ApiCollectionSummary[]>([])
const heroCollection = ref<ApiCollectionSummary | null>(null)
const genres = ref<readonly string[]>([])
const selectedGenre = ref('全部') // 风格 chips 当前选中项（本地过滤最近加入）
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
    artist: item.artists.length ? item.artists.join(', ') : '未知艺人',
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
    title: '移出我的曲库',
    content: `将「${item.title}」移出我的曲库？曲目本体保留，仅移除引用。`,
    confirmText: '移出',
    success: async ({ confirm }) => {
      if (!confirm) return
      removingId.value = item.trackId
      try {
        await removeFromLibrary(item.trackId)
        uni.showToast({ title: '已移出', icon: 'none' })
      } catch {
        uni.showToast({ title: '操作失败，请稍后再试', icon: 'none' })
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
 * 点击头像：未登录跳登录页；已登录滑出个人抽屉（顶部头像与“我的”tab 资料卡共用）。
 */
function onAvatarTap() {
  if (!isAuthenticated.value) {
    uni.navigateTo({ url: '/pages/auth/login' })
    return
  }
  drawerVisible.value = true
}

// 风格 chips：在服务端风格前补一个“全部”。后端空集合时回退到默认风格列表，避免空状态
// 仅剩“全部”而显得页面不完整
const DEFAULT_GENRES = ['华语', '欧美', '日语', '电子', '民谣', '摇滚', 'R&B', '古风']
const genreChips = computed(() => {
  const list = genres.value.length ? genres.value : DEFAULT_GENRES
  return ['全部', ...list]
})

// 静态兜底精选合集：后端无数据时仍用于填满「精选专题 / 编辑精选」区块，避免页面空白。
// 点击默认合集（id 以 default- 开头）跳转对应风格浏览页，而非不存在的合集详情。
const DEFAULT_COLLECTIONS: ApiCollectionSummary[] = [
  { id: 'default-huayu', title: '华语流行精选', description: '最懂华语情怀的旋律', artworkUrl: null, trackCount: 50 },
  { id: 'default-oumei', title: '欧美金曲', description: 'Billboard 热歌精选', artworkUrl: null, trackCount: 50 },
  { id: 'default-dianzi', title: '电子律动', description: '深夜节拍不间断', artworkUrl: null, trackCount: 50 },
  { id: 'default-minyao', title: '民谣故事', description: '一把吉他一杯酒', artworkUrl: null, trackCount: 50 },
]
// 精选专题：后端无合集时回退到内置兜底合集
const displayCollections = computed<readonly ApiCollectionSummary[]>(() =>
  collections.value.length ? collections.value : DEFAULT_COLLECTIONS,
)
// 编辑精选大卡：优先真实头图专题 / 精选曲目，再回退到内置兜底合集，保证始终有内容
const effectiveFeatured = computed<ApiCollectionSummary | Track | null>(() =>
  heroCollection.value ?? featured.value ?? DEFAULT_COLLECTIONS[0] ?? null,
)
const featuredCollection = computed(() =>
  effectiveFeatured.value && 'trackCount' in effectiveFeatured.value
    ? (effectiveFeatured.value as ApiCollectionSummary)
    : null,
)
const featuredTrack = computed(() =>
  effectiveFeatured.value && !('trackCount' in effectiveFeatured.value)
    ? (effectiveFeatured.value as Track)
    : null,
)
// 最近加入列表：选中“全部”时原样展示，否则按风格本地过滤
const visibleRecent = computed(
  () =>
    selectedGenre.value === '全部'
    ? recentTracks.value
    : recentTracks.value.filter((track) => track.genres.includes(selectedGenre.value)),
)
// 头像首字母：取显示名首字符大写，缺省回退为 L
const avatarLetter = computed(() => currentUser.value?.displayName?.slice(0, 1).toUpperCase() || 'L')

/**
 * 加载“为你”首页 Feed（精选曲目/最近加入/专题）
 * 关键副作用：失败时置 loadError，页面展示曲库连接失败提示
 */
async function loadHome() {
  loadError.value = false
  try {
    const home = await getHomeFeed()
    featured.value = home.featured
    recentTracks.value = home.recentlyAdded
    collections.value = home.collections
    heroCollection.value = home.collections[0] ?? null
  } catch {
    loadError.value = true
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
// 卡片右侧的 3 张迷你封面预览（堆叠进今日灵感）
const dailyPreview = computed(() => dailyTracks.value.slice(0, 3))

// —— 「今日灵感」时间驱动个性化 ——
/** 当前时段（hour-based）：决定问候语、氛围标签、首选曲目风格 */
const nowHour = new Date().getHours()
const timeGreeting = computed(() => {
  if (nowHour < 6) return { label: '夜深了', tone: '夜深了，来点安静的' }
  if (nowHour < 11) return { label: '早安', tone: '早上好，今天从一首歌开始' }
  if (nowHour < 14) return { label: '午好', tone: '午后放松一下' }
  if (nowHour < 18) return { label: '下午好', tone: '下午茶时间' }
  if (nowHour < 22) return { label: '晚上好', tone: '夜晚的旋律' }
  return { label: '夜深了', tone: '夜深了，来点安静的' }
})
/** 时段氛围标签 — 搭配灵感卡片副标题 */
const moodTag = computed(() => {
  if (nowHour < 6) return { time: '深夜', mood: '安静' }
  if (nowHour < 11) return { time: '清晨', mood: '轻快' }
  if (nowHour < 14) return { time: '午后', mood: '松弛' }
  if (nowHour < 18) return { time: '下午', mood: '松弛' }
  return { time: '夜晚', mood: '温柔' }
})
/** 灵感主卡曲目：优先取每日推荐中匹配时段 mood 的曲目，否则 fallback 到第一首 */
const inspirationTrack = computed<Track | null>(() => {
  const mood = moodTag.value.mood
  if (dailyTracks.value.length) {
    const matched = dailyTracks.value.find((t) => t.mood === mood || t.genres.some((g) => g.includes(mood)))
    return matched ?? dailyTracks.value[0] ?? null
  }
  return featured.value ?? recentTracks.value[0] ?? null
})

/**
 * 加载每日推荐（登录后为个性化结果，未登录为热度/最新兜底）。
 * 失败时以“最近加入”兜底，保证卡片始终有内容可点。
 */
async function loadDaily() {
  dailyLoading.value = true
  try {
    const result = await getDailyRecommendations(30)
    dailyTracks.value = result.tracks
  } catch {
    dailyTracks.value = recentTracks.value.slice(0, 30)
  } finally {
    dailyLoading.value = false
  }
}

/** 进入每日推荐列表页 */
function goDaily() {
  uni.navigateTo({ url: '/pages/recommend/daily' })
}

/** 播放「今日灵感」主打曲目 */
function playInspiration() {
  if (inspirationTrack.value) {
    // 以每日推荐列表为队列、灵感曲目为起点
    const queue = dailyTracks.value.length ? dailyTracks.value : (featured.value ? [featured.value] : recentTracks.value)
    playTrack(inspirationTrack.value, queue)
  } else {
    goDaily()
  }
}

/**
 * 加载风格 chips
 * 后端没有风格标签时保持空集合（仅剩“全部”），页面展示“曲库中还没有风格标签。”
 */
async function loadGenreChips() {
  try {
    genres.value = await getGenres()
  } catch {
    // 接口异常：保持空集合，不做演示数据兜底
    genres.value = []
  }
}

/**
 * 监听搜索框输入：仅在“发现”tab 生效，280ms 防抖后调用搜索接口
 * 关键词清空时立即重置结果；每次输入先清掉上一个防抖定时器
 */
watch(query, (value) => {
  if (activeTab.value !== '发现') return
  if (searchTimer) clearTimeout(searchTimer)
  const keyword = value.trim()
  if (!keyword) {
    searchResults.value = []
    searched.value = false
    searchPending.value = false
    return
  }
  searchPending.value = true
  // 防抖：停顿 280ms 后才真正发起搜索请求
  searchTimer = setTimeout(async () => {
    try {
      searchResults.value = await searchTracks(keyword)
      searched.value = true
    } catch {
      searchResults.value = []
      searched.value = true
    } finally {
      searchPending.value = false
    }
  }, 280)
})

/**
 * 切换底部 tab；切到“我的”时按需拉取我的歌单，切到“曲库”时按需拉取曲库引用
 * @param tab 目标 tab 名称
 */
function setTab(tab: string) {
  activeTab.value = tab
  if (tab === '我的') void loadLibrary()
  if (tab === '曲库' && isAuthenticated.value) void loadMyLibrary(true)
}

/** 点击顶部搜索框：聚焦即跳到“发现”tab（搜索词仍由输入框双向绑定） */
function goSearch() {
  activeTab.value = '发现'
}

/**
 * 加载“我的”页数据（收藏列表 + 我的歌单）
 * 前置条件：已登录、未在加载中、后端开放了 userCollections 能力，否则直接跳过
 */
async function loadLibrary() {
  if (!isAuthenticated.value || libraryLoading.value || !capabilities.capabilities.userCollections) return
  libraryLoading.value = true
  try {
    const [, playlists] = await Promise.all([loadFavorites(true), getPlaylists()])
    myPlaylists.value = playlists.items
  } catch {
    // 忽略：收藏页展示错误提示即可
  } finally {
    libraryLoading.value = false
  }
}

// 每次页面重新显示（如从登录页/上传页返回）：若停留在“我的”/“曲库”tab 则刷新对应数据，
// 保证上传完成、登录状态变化后列表立即更新
onShow(() => {
  if (activeTab.value === '我的') void loadLibrary()
  if (activeTab.value === '曲库' && isAuthenticated.value) void loadMyLibrary(true)
  void refreshUnreadCount() // 从消息页返回/登录返回时刷新角标；未登录时内部静默失败
})

// 首次挂载：并行拉取首页 Feed 与风格；已登录时预加载收藏数据
onMounted(() => {
  void loadHome()
  void loadGenreChips()
  void loadDaily()
  if (isAuthenticated.value) void loadLibrary()
})

/**
 * 点击头图大卡：有头图专题则进入专题详情（默认合集跳风格页），否则直接播放精选曲目
 */
function tapHero() {
  if (featuredCollection.value) {
    if (featuredCollection.value.id.startsWith('default-')) goGenre(featuredCollection.value.title)
    else uni.navigateTo({ url: `/pages/catalog/collection?id=${featuredCollection.value.id}` })
    return
  }
  if (featuredTrack.value) playTrack(featuredTrack.value, recentTracks.value)
}

/**
 * 点击精选专题卡：真实合集进入详情，内置兜底合集（default-）跳对应风格浏览页
 * @param collection 专题摘要
 */
function tapCollection(collection: ApiCollectionSummary) {
  if (collection.id.startsWith('default-')) {
    goGenre(collection.title)
    return
  }
  goCollection(collection)
}

/** 播放“最近加入”当前可见列表（按风格过滤后的结果）整体入队 */
function playAllRecent() {
  if (visibleRecent.value.length) playQueue(visibleRecent.value)
}

/**
 * 进入精选专题详情
 * @param collection 专题摘要
 */
function goCollection(collection: ApiCollectionSummary) {
  uni.navigateTo({ url: `/pages/catalog/collection?id=${collection.id}` })
}

/**
 * 进入某风格的曲目列表页（genre 与导航栏标题均通过 query 透传）
 * @param genre 风格名
 */
function goGenre(genre: string) {
  uni.navigateTo({ url: `/pages/catalog/tracks?genre=${encodeURIComponent(genre)}&title=${encodeURIComponent(genre)}` })
}

/** 进入歌曲需求池；功能开关关闭时 toast 拦截 */
function goRequests() {
  if (!capabilities.capabilities.songRequests) {
    uni.showToast({ title: '歌曲需求暂未开放', icon: 'none' })
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
    uni.showToast({ title: '名字不能为空', icon: 'none' })
    return
  }
  if (creatingPlaylist.value) return
  creatingPlaylist.value = true
  try {
    await createPlaylist(name)
    uni.showToast({ title: '歌单已创建', icon: 'none' })
    newPlaylistName.value = ''
    createSheetVisible.value = false
    await loadLibrary()
  } catch {
    uni.showToast({ title: '创建失败，请稍后再试', icon: 'none' })
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

    <scroll-view class="page-scroll" scroll-y :show-scrollbar="false">
      <!-- 顶部品牌栏：Logo + 头像（点击滑出个人抽屉） -->
      <view class="topbar">
        <view class="brand-lockup">
          <view class="brand-mark"><AudioLines :size="19" :stroke-width="2.7" /></view>
          <text class="brand-name">shine<span>.</span></text>
        </view>
        <view class="topbar-actions">
          <view class="avatar" @tap="onAvatarTap">{{ avatarLetter }}</view>
        </view>
      </view>

      <!-- 问候标题 + 今日灵感卡 -->
      <view class="page-heading">
        <view>
          <text class="eyebrow">SHINE MUSIC</text>
          <text class="greeting">{{ timeGreeting.label }}，{{ isAuthenticated ? currentUser?.displayName ?? '朋友' : '朋友' }}</text>
          <text class="greeting-sub">{{ timeGreeting.tone }}</text>
        </view>
      </view>

      <!-- 今日灵感卡片：时间驱动 + 氛围标签 + 主打曲目 + 播放按钮 + 更多入口 -->
      <view v-if="activeTab === '为你'" class="inspiration-card" @tap="goDaily">
        <view class="inspiration-left">
          <view class="inspiration-badge">
            <Sparkles :size="13" />
            <text>{{ moodTag.time }} · {{ moodTag.mood }}</text>
          </view>
          <text class="inspiration-title">今日灵感</text>
          <view v-if="inspirationTrack" class="inspiration-track">
            <text class="inspiration-name">{{ inspirationTrack.title }}</text>
            <text class="inspiration-artist">{{ inspirationTrack.artist }}</text>
          </view>
          <text v-else class="inspiration-empty">正在为你挑选…</text>
          <view class="inspiration-actions">
            <view class="inspiration-play" @tap.stop="playInspiration">
              <Play :size="16" fill="currentColor" /><text>播放</text>
            </view>
            <view class="inspiration-more">
              <text>更多</text><ChevronRight :size="14" />
            </view>
          </view>
        </view>
        <view class="inspiration-right">
          <view v-if="inspirationTrack?.cover" class="inspiration-cover">
            <CoverArt :src="inspirationTrack.cover" :rounded="26" :icon-size="46" />
          </view>
          <view v-else class="inspiration-cover inspiration-cover-empty">
            <view class="inspiration-cover-pattern"></view>
            <Music2 :size="52" :stroke-width="1.6" />
            <text class="inspiration-cover-hint">♪</text>
          </view>
          <view v-if="dailyPreview.length > 1" class="inspiration-stack">
            <CoverArt :src="dailyPreview[1].cover" :rounded="16" :icon-size="22" />
          </view>
          <view v-if="dailyPreview.length > 2" class="inspiration-stack-2">
            <CoverArt :src="dailyPreview[2].cover" :rounded="14" :icon-size="18" />
          </view>
        </view>
      </view>

      <!-- 搜索入口：点击/聚焦切到“发现”tab -->
      <view class="search-box" @tap="goSearch">
        <Search :size="19" :stroke-width="2" />
        <input v-model="query" confirm-type="search" placeholder="搜索歌曲、歌手或专辑" @focus="goSearch" />
      </view>

      <!-- 为你 -->
      <template v-if="activeTab === '为你'">
        <!-- 编辑精选（弱化大卡）：紧凑横卡，优先展示专题，无专题时播放精选曲目 -->
        <view class="feature-section">
          <view class="section-heading feature-title-row">
            <view>
              <text class="section-kicker">YOUR DAILY SPARK</text>
              <text class="section-title">今天，为你发现</text>
            </view>
          </view>
          <view class="featured-card" @tap="tapHero">
            <view class="featured-thumb">
              <CoverArt :src="featuredCollection ? coverOf(featuredCollection) : featuredTrack?.cover ?? ''" :rounded="16" :icon-size="34" />
            </view>
            <view class="featured-info">
              <view class="feature-label"><Sparkles :size="13" /><text>编辑精选</text></view>
              <text class="featured-name">{{ featuredCollection ? featuredCollection.title : featuredTrack?.title }}</text>
              <text v-if="featuredCollection?.description" class="featured-caption">{{ featuredCollection.description }}</text>
              <view class="feature-play"><Music2 :size="16" /><text>{{ featuredCollection ? '查看专题' : '立即播放' }}</text></view>
            </view>
            <view v-if="featuredCollection" class="feature-count">
              <Music2 :size="13" /><text>{{ featuredCollection.trackCount }} 首</text>
            </view>
          </view>
        </view>

        <!-- 最近加入曲目：主内容区（含网络错误 / 空数据 / 列表三种状态） -->
        <view class="recommend-section">
          <view class="section-heading recommend-heading">
            <view><text class="section-kicker">RECENTLY ADDED</text><text class="section-title">最近加入</text></view>
            <view class="round-action" @tap="playAllRecent"><Music2 :size="17" /></view>
          </view>
          <view v-if="loadError" class="recommend-empty" @tap="retryHome">
            <text class="recommend-empty-title">曲库暂时连不上</text>
            <text class="recommend-empty-sub">先逛逛下面的精选 · 轻点重新连接</text>
          </view>
          <view v-else-if="!visibleRecent.length" class="recommend-empty">
            <text class="recommend-empty-title">还没有已发布的曲目</text>
            <text class="recommend-empty-sub">去发现更多好音乐</text>
          </view>
          <view v-else class="track-list">
            <TrackRow
              v-for="(track, index) in visibleRecent"
              :key="track.id"
              :track="track"
              :index="index"
              :queue="visibleRecent"
            />
          </view>
        </view>

        <!-- 精选专题横滑列表（次要区块，后端无数据时回退内置兜底合集） -->
        <view class="collection-section">
          <view class="section-heading">
            <view><text class="section-kicker">EDITORIAL PICKS</text><text class="section-title">精选专题</text></view>
          </view>
          <scroll-view class="collection-scroll" scroll-x :show-scrollbar="false">
            <view class="collection-list">
              <view
                v-for="collection in displayCollections"
                :key="collection.id"
                class="collection-card"
                @tap="tapCollection(collection)"
              >
                <view class="collection-cover"><CoverArt :src="coverOf(collection)" :rounded="20" :icon-size="24" /></view>
                <text class="collection-title">{{ collection.title }}</text>
                <text class="collection-meta">{{ collection.trackCount }} 首</text>
              </view>
            </view>
          </scroll-view>
        </view>

        <!-- 风格 chips（次要区块）：选中后本地过滤上方“最近加入” -->
        <view class="mood-section">
          <view class="section-heading">
            <view><text class="section-kicker">PICK YOUR GENRE</text><text class="section-title">按风格听听</text></view>
          </view>
          <scroll-view class="mood-scroll" scroll-x :show-scrollbar="false">
            <view class="mood-list">
              <view
                v-for="(genre, index) in genreChips"
                :key="genre"
                class="mood-chip"
                :class="[`mood-${index % 5}`, { selected: selectedGenre === genre }]"
                @tap="selectedGenre = genre"
              >
                <text class="mood-dot"></text><text>{{ genre }}</text>
              </view>
            </view>
          </scroll-view>
        </view>
      </template>

      <!-- 发现 -->
      <template v-else-if="activeTab === '发现'">
        <!-- 未输入关键词：推荐歌单（首个大卡 + 其余横滑）+ 风格宫格浏览 -->
        <view v-if="!query.trim()" class="discover-section">
          <view v-if="collections.length" class="discover-block">
            <view class="section-heading">
              <view>
                <text class="section-kicker">PLAYLISTS</text>
                <text class="section-title">推荐歌单</text>
              </view>
            </view>

            <!-- 首个歌单：大卡（歌单封面 + 推荐语 + 曲目数），点击进入歌单详情 -->
            <view class="playlist-hero" @tap="goCollection(collections[0])">
              <view class="playlist-hero-cover">
                <CoverArt :src="coverOf(collections[0])" :rounded="18" :icon-size="26" />
              </view>
              <view class="playlist-hero-copy">
                <text class="playlist-hero-title">{{ collections[0].title }}</text>
                <text class="playlist-hero-desc">{{ collections[0].description || '编辑精选' }}</text>
                <text class="playlist-hero-meta">{{ collections[0].trackCount }} 首</text>
              </view>
            </view>

            <!-- 其余歌单：横滑小卡 -->
            <scroll-view v-if="collections.length > 1" class="collection-scroll" scroll-x :show-scrollbar="false">
              <view class="collection-list">
                <view
                  v-for="collection in collections.slice(1)"
                  :key="collection.id"
                  class="collection-card"
                  @tap="goCollection(collection)"
                >
                  <view class="collection-cover"><CoverArt :src="coverOf(collection)" :rounded="20" :icon-size="24" /></view>
                  <text class="collection-title">{{ collection.title }}</text>
                  <text class="collection-meta">{{ collection.trackCount }} 首</text>
                </view>
              </view>
            </scroll-view>
          </view>

          <view class="discover-block">
            <view class="section-heading">
              <view><text class="section-kicker">BROWSE GENRES</text><text class="section-title">浏览风格</text></view>
            </view>
            <view class="genre-grid">
              <view v-for="genre in genreChips.filter((item) => item !== '全部')" :key="genre" class="genre-chip" @tap="goGenre(genre)">
                <text>{{ genre }}</text>
                <ChevronRight :size="15" />
              </view>
              <view v-if="genreChips.length === 1" class="inline-error">曲库中还没有风格标签。</view>
            </view>
          </view>
        </view>

        <!-- 有关键词：防抖搜索结果，空结果时引导登录用户去歌曲需求池 -->
        <view v-else class="recommend-section search-results">
          <view class="section-heading recommend-heading">
            <view>
              <text class="section-kicker">SEARCH RESULTS</text>
              <text class="section-title">“{{ query.trim() }}” 的结果</text>
            </view>
          </view>
          <view v-if="searchPending" class="inline-error">正在搜索…</view>
          <view v-else-if="searched && !searchResults.length" class="no-results">
            <text>没有找到匹配的歌曲，换个关键词试试。</text>
            <view v-if="isAuthenticated" class="request-link" @tap="goRequests">
              <Inbox :size="15" /><text>去歌曲需求池告诉管理员</text>
            </view>
          </view>
          <view v-else class="track-list">
            <TrackRow
              v-for="(track, index) in searchResults"
              :key="track.id"
              :track="track"
              :index="index"
              :queue="searchResults"
            />
          </view>
        </view>
      </template>

      <!-- 曲库：我的上传/引用曲目（含私有与公开） -->
      <template v-else-if="activeTab === '曲库'">
        <view class="library-wrap">
          <!-- 未登录：登录引导卡 -->
          <view v-if="!isAuthenticated" class="login-card">
            <view class="login-icon"><Library :size="22" /></view>
            <text class="login-title">登录查看我的曲库</text>
            <text class="login-copy">登录后管理你上传的歌曲，私有歌曲仅本人可听</text>
            <view class="login-button" @tap="goLogin"><text>登录 / 邀请注册</text></view>
          </view>

          <!-- 已登录：标题行（上传入口受开关控制）/ 加载 / 空态 / 列表 -->
          <template v-else>
            <view class="section-heading">
              <view>
                <text class="section-kicker">MY LIBRARY</text>
                <text class="section-title">我的曲库</text>
              </view>
              <view v-if="canUserUpload" class="create-playlist" @tap="goUpload">
                <Upload :size="17" /><text>上传</text>
              </view>
              <text v-else class="library-count">{{ myLibraryItems.length }} 首</text>
            </view>

            <view v-if="myLibraryLoading && !myLibraryItems.length" class="inline-error">正在加载…</view>
            <view v-else-if="!myLibraryItems.length" class="empty-state">
              <view class="empty-icon"><Library :size="23" /></view>
              <text class="empty-title">曲库还没有歌曲</text>
              <text class="empty-copy">上传本地音频后会安静地收在这里</text>
              <view v-if="canUserUpload" class="empty-upload" @tap="goUpload">
                <Upload :size="16" /><text>上传本地音频</text>
              </view>
            </view>

            <template v-else>
              <view class="lib-head"><text class="lib-count">共 {{ myLibraryItems.length }} 首</text></view>
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
                    <text v-if="!item.isPublic" class="lib-badge private">私有</text>
                    <text v-else class="lib-badge public">已公开</text>
                  </view>
                  <text class="lib-artist">{{ item.artists.join(', ') || '未知艺人' }}</text>
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

      <!-- 我的（原“收藏”tab：个人中心聚合页） -->
      <template v-else>
        <view class="library-wrap">
          <!-- 已登录：资料卡（点击滑出个人抽屉）/ 我的歌单 / 喜欢的音乐 -->
          <template v-if="isAuthenticated">
            <view class="profile-card" @tap="onAvatarTap">
              <view class="profile-avatar">{{ avatarLetter }}</view>
              <view class="profile-copy">
                <text class="profile-name">{{ currentUser?.displayName }}</text>
                <text class="profile-email">{{ currentUser?.email }}</text>
              </view>
              <ChevronRight :size="18" class="profile-chevron" />
            </view>

            <!-- 快捷入口：圆形图标行（消息带未读角标） -->
            <view class="quick-circles">
              <view class="quick-circle" @tap="goPage('/pages/notifications/index')">
                <view class="qc-icon qc-notify"><Bell :size="20" /></view>
                <text v-if="unreadCount > 0" class="qc-badge">{{ unreadLabel }}</text>
                <text class="qc-label">消息</text>
              </view>
              <view class="quick-circle" @tap="goPage('/pages/library/favorites')">
                <view class="qc-icon qc-favorite"><Heart :size="20" /></view>
                <text class="qc-label">我的收藏</text>
              </view>
              <view class="quick-circle" @tap="goPage('/pages/history/index')">
                <view class="qc-icon qc-history"><History :size="20" /></view>
                <text class="qc-label">播放历史</text>
              </view>
              <view class="quick-circle" @tap="goPage('/pages/social/follow')">
                <view class="qc-icon qc-follow"><UserRound :size="20" /></view>
                <text class="qc-label">关注粉丝</text>
              </view>
            </view>

            <!-- 分组入口：音乐库 / 设置 -->
            <view class="entry-group">
              <text class="entry-group-title">音乐库</text>
              <view class="entry-row" @tap="goPage('/pages/library/upload')">
                <view class="entry-icon ei-upload"><Upload :size="18" /></view>
                <text class="entry-name">上传本地音频</text>
                <ChevronRight :size="16" class="profile-chevron" />
              </view>
              <view class="entry-row" @tap="goRequests">
                <view class="entry-icon ei-request"><Inbox :size="18" /></view>
                <text class="entry-name">歌曲需求池</text>
                <ChevronRight :size="16" class="profile-chevron" />
              </view>
            </view>
            <view class="entry-group">
              <text class="entry-group-title">设置</text>
              <view class="entry-row" @tap="goPage('/pages/settings/theme')">
                <view class="entry-icon ei-theme"><Palette :size="18" /></view>
                <text class="entry-name">主题外观</text>
                <ChevronRight :size="16" class="profile-chevron" />
              </view>
              <view class="entry-row" @tap="goPage('/pages/profile/index')">
                <view class="entry-icon ei-profile"><UserCog :size="18" /></view>
                <text class="entry-name">个人资料</text>
                <ChevronRight :size="16" class="profile-chevron" />
              </view>
            </view>

            <view v-if="capabilities.capabilities.userCollections" class="library-block">
              <view class="section-heading">
                <view><text class="section-kicker">MY PLAYLISTS</text><text class="section-title">我的歌单</text></view>
                <view class="create-playlist" @tap="openCreatePlaylist"><Plus :size="17" /><text>新建</text></view>
              </view>
              <view v-if="!myPlaylists.length" class="inline-error">还没有歌单，点“新建”创建一个。</view>
              <view
                v-for="playlist in myPlaylists"
                :key="playlist.id"
                class="playlist-row"
                @tap="goPlaylist(playlist)"
              >
                <view class="playlist-icon">
                  <image v-if="playlist.artworkUrl" class="playlist-thumbnail" :src="resolveUrl(playlist.artworkUrl)" mode="aspectFill" />
                  <ListMusic v-else :size="19" />
                </view>
                <view class="playlist-copy">
                  <text class="playlist-name">{{ playlist.name }}</text>
                  <text class="playlist-meta">{{ playlist.trackCount }} 首</text>
                </view>
                <ChevronRight :size="16" class="profile-chevron" />
              </view>
            </view>
            <view v-else class="inline-error">收藏与歌单功能暂未开放。</view>

            <view class="library-block">
              <view class="section-heading">
                <view><text class="section-kicker">YOUR COLLECTION</text><text class="section-title">喜欢的音乐</text></view>
                <text class="library-count">{{ favoriteTracks.length }} 首</text>
              </view>
              <view v-if="!favoriteTracks.length" class="empty-state">
                <view class="empty-icon"><Heart :size="23" /></view>
                <text class="empty-title">收藏几首，慢慢听</text>
                <text class="empty-copy">点曲目旁的爱心，会安静地收在这里</text>
              </view>
              <TrackRow
                v-for="(track, index) in favoriteTracks"
                :key="track.id"
                :track="track"
                :index="index"
                :queue="favoriteTracks"
              />
            </view>
          </template>

          <!-- 未登录：登录引导卡（本地缓存的喜欢的音乐仍可展示） -->
          <template v-else>
            <view class="login-card">
              <view class="login-icon"><AudioLines :size="22" /></view>
              <text class="login-title">登录 Shine Music</text>
              <text class="login-copy">登录后同步收藏与歌单，还能提交歌曲需求和投票</text>
              <view class="login-button" @tap="goLogin"><text>登录 / 邀请注册</text></view>
            </view>
            <view v-if="favoriteTracks.length" class="library-block">
              <view class="section-heading">
                <view><text class="section-kicker">YOUR COLLECTION</text><text class="section-title">喜欢的音乐</text></view>
              </view>
              <TrackRow
                v-for="(track, index) in favoriteTracks"
                :key="track.id"
                :track="track"
                :index="index"
                :queue="favoriteTracks"
              />
            </view>
          </template>

          <!-- 歌曲需求池入口横幅：已登录用户在上方「音乐库」分组里已有入口，此处仅保留给未登录用户 -->
          <view v-if="capabilities.capabilities.songRequests && !isAuthenticated" class="request-banner" @tap="goRequests">
            <view class="request-icon"><Inbox :size="19" /></view>
            <view class="upload-copy">
              <text class="upload-title">想听的歌，告诉我们</text>
              <text class="upload-subtitle">歌曲需求池 · 投票决定优先级</text>
            </view>
            <ChevronRight :size="17" class="profile-chevron" />
          </view>
        </view>
      </template>

      <view class="bottom-spacer"></view>
    </scroll-view>

    <!-- 全局迷你播放器（有当前曲目时内部自行显示） -->
    <PlayerBar :with-tabbar="true" />

    <!-- 头像抽屉：点击头像滑出的个人菜单（QQ 式） -->
    <ProfileDrawer v-model="drawerVisible" />

    <!-- 快捷面板：点击 tabbar 中间凸起按钮弹出 -->
    <QuickPanel v-model="panelVisible" />

    <!-- 底部自定义 tabbar：为你 / 发现 / [消息凸起按钮] / 曲库 / 我的 -->
    <view class="tabbar">
      <view
        v-for="tab in tabs.slice(0, 2)"
        :key="tab"
        class="tab-item"
        :class="{ active: activeTab === tab }"
        @tap="setTab(tab)"
      >
        <Sparkles v-if="tab === '为你'" :size="19" />
        <Search v-else :size="19" />
        <text>{{ tab }}</text>
      </view>
      <view class="tab-fab" @tap="onFabTap">
        <Bell :size="22" />
        <text v-if="unreadCount > 0" class="tab-fab-badge">{{ unreadLabel }}</text>
      </view>
      <view
        v-for="tab in tabs.slice(2)"
        :key="tab"
        class="tab-item"
        :class="{ active: activeTab === tab }"
        @tap="setTab(tab)"
      >
        <Library v-if="tab === '曲库'" :size="19" />
        <User v-else :size="19" />
        <text>{{ tab }}</text>
      </view>
    </view>

    <!-- 新建歌单弹层（受控 Dialog，替代 uni.showModal editable） -->
    <view v-if="createSheetVisible" class="sheet-mask" @tap="closeCreatePlaylist">
      <view class="sheet-dialog" @tap.stop>
        <text class="sheet-title">新建歌单</text>
        <input
          v-model="newPlaylistName"
          class="sheet-input"
          type="text"
          maxlength="60"
          placeholder="给歌单起个名字"
          :focus="true"
          confirm-type="done"
          @confirm="confirmCreatePlaylist"
        />
        <view class="sheet-actions">
          <view class="sheet-btn ghost" @tap="closeCreatePlaylist">取消</view>
          <view class="sheet-btn primary" :class="{ disabled: creatingPlaylist }" @tap="confirmCreatePlaylist">
            {{ creatingPlaylist ? '创建中…' : '创建' }}
          </view>
        </view>
      </view>
    </view>
  </view>
</template>

<style lang="scss">
/* 首页（方向 B · 玻璃质感）：完整页面背景（自定义主题下为壁纸）+ 彩色光晕 + 毛玻璃卡片 */
.app-shell { position: relative; width: 100%; min-height: 100vh; background: var(--sm-page-bg); overflow: hidden; }
.page-scroll { position: relative; z-index: 1; height: 100vh; }
.topbar { display: flex; align-items: center; justify-content: space-between; padding: calc(env(safe-area-inset-top) + 30rpx) 44rpx 0; }
.brand-lockup { display: flex; align-items: center; gap: 11rpx; }
.brand-mark { display: grid; place-items: center; width: 40rpx; height: 40rpx; border-radius: 13rpx; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); color: var(--sm-accent); -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.brand-name { font-family: 'Manrope', sans-serif; font-size: 36rpx; line-height: 1; font-weight: 800; color: var(--sm-text); }
.brand-name span { color: var(--sm-accent); }
.topbar-actions { display: flex; align-items: center; gap: 14rpx; }
.avatar { display: grid; place-items: center; width: 68rpx; height: 68rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text); font-family: 'DM Sans', sans-serif; font-size: 25rpx; font-weight: 700; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); box-shadow: 0 4rpx 14rpx var(--sm-art-shadow); }
.page-heading { display: flex; align-items: end; padding: 50rpx 44rpx 0; }
.eyebrow, .section-kicker { display: block; color: var(--sm-accent); font-family: 'DM Sans', sans-serif; font-size: 17rpx; font-weight: 700; letter-spacing: 1.2rpx; }
.greeting { display: block; margin-top: 11rpx; font-size: 42rpx; line-height: 1.25; font-weight: 700; color: var(--sm-text); }
.greeting-emphasis { color: var(--sm-accent); }
.greeting-sub { display: block; margin-top: 8rpx; color: var(--sm-text-2); font-size: 18rpx; }

/* 今日灵感卡片（Hero 大卡） */
.inspiration-card { display: flex; align-items: stretch; gap: 30rpx; margin: 40rpx 40rpx 0; padding: 36rpx 34rpx 36rpx 38rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 34rpx; background: linear-gradient(135deg, var(--sm-cover-bg), rgba(0, 0, 0, .12)); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); box-shadow: 0 26rpx 60rpx var(--sm-art-shadow); transition: transform .2s ease; }
.inspiration-card:active { transform: scale(.985); }
.inspiration-left { display: flex; flex: 1; min-width: 0; flex-direction: column; justify-content: space-between; gap: 16rpx; }
.inspiration-badge { display: inline-flex; align-items: center; gap: 6rpx; width: fit-content; padding: 6rpx 16rpx; border-radius: 30rpx; background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 16rpx; font-weight: 700; letter-spacing: 1rpx; }
.inspiration-title { color: var(--sm-text); font-size: 30rpx; font-weight: 800; letter-spacing: .5rpx; }
.inspiration-track { display: flex; flex-direction: column; gap: 6rpx; }
.inspiration-name { overflow: hidden; color: var(--sm-text); font-size: 34rpx; font-weight: 800; text-overflow: ellipsis; white-space: nowrap; }
.inspiration-artist { overflow: hidden; color: var(--sm-text-2); font-size: 19rpx; text-overflow: ellipsis; white-space: nowrap; }
.inspiration-empty { color: var(--sm-text-3); font-size: 18rpx; }
.inspiration-actions { display: flex; align-items: center; gap: 18rpx; margin-top: 8rpx; }
.inspiration-play { display: flex; align-items: center; gap: 9rpx; padding: 14rpx 30rpx; border-radius: 40rpx; background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); font-size: 19rpx; font-weight: 700; box-shadow: 0 8rpx 24rpx var(--sm-accent-glow); }
.inspiration-more { display: inline-flex; align-items: center; gap: 4rpx; color: var(--sm-text-2); font-size: 17rpx; }
.inspiration-right { position: relative; flex: none; width: 244rpx; height: 244rpx; }
.inspiration-cover { position: relative; width: 100%; height: 100%; border-radius: 28rpx; border: 2rpx solid var(--sm-border-strong); box-shadow: 0 18rpx 44rpx var(--sm-art-shadow); overflow: hidden; background: var(--sm-cover-bg); }
.inspiration-cover .cover-art { width: 100%; height: 100%; border-radius: inherit; border: none; box-shadow: none; }
.inspiration-cover-empty { display: grid; place-items: center; background: linear-gradient(135deg, var(--sm-accent) 0%, var(--sm-accent-2) 100%); color: rgba(255, 255, 255, .92); }
.inspiration-cover-pattern { position: absolute; inset: 0; background: radial-gradient(circle at 28% 24%, rgba(255, 255, 255, .28) 0%, transparent 55%), radial-gradient(circle at 78% 82%, rgba(255, 255, 255, .18) 0%, transparent 50%), repeating-linear-gradient(45deg, rgba(255, 255, 255, .05) 0 8rpx, transparent 8rpx 16rpx); }
.inspiration-cover-hint { position: absolute; right: 18rpx; bottom: 14rpx; font-size: 44rpx; font-weight: 700; color: rgba(255, 255, 255, .55); font-family: Georgia, serif; }
.inspiration-stack { position: absolute; top: -18rpx; right: -38rpx; width: 96rpx; height: 96rpx; border-radius: 18rpx; border: 2rpx solid var(--sm-border-strong); box-shadow: 0 8rpx 22rpx var(--sm-art-shadow); overflow: hidden; background: var(--sm-surface-3); transform: rotate(8deg); }
.inspiration-stack-2 { position: absolute; bottom: -20rpx; left: -36rpx; width: 74rpx; height: 74rpx; border-radius: 16rpx; border: 2rpx solid var(--sm-border-strong); box-shadow: 0 6rpx 18rpx var(--sm-art-shadow); overflow: hidden; background: var(--sm-surface-3); transform: rotate(-10deg); }
.inspiration-stack .cover-art, .inspiration-stack-2 .cover-art { width: 100%; height: 100%; border-radius: inherit; border: none; box-shadow: none; }
.search-box { display: flex; align-items: center; gap: 18rpx; height: 90rpx; margin: 44rpx 40rpx 0; padding: 0 25rpx; border: 1rpx solid var(--sm-border); border-radius: 23rpx; background: var(--sm-surface); color: var(--sm-text-2); -webkit-backdrop-filter: blur(14px); backdrop-filter: blur(14px); }
.search-box input { flex: 1; min-width: 0; height: 100%; color: var(--sm-text); font-size: 23rpx; }
.search-box input::placeholder { color: var(--sm-text-3); }
.feature-section, .mood-section, .recommend-section, .collection-section, .library-wrap, .discover-section { padding: 0 40rpx; }
.feature-section { margin-top: 64rpx; }
.collection-section, .discover-block { margin-top: 72rpx; }
.section-heading { display: flex; align-items: end; justify-content: space-between; }
.section-title { display: block; margin-top: 9rpx; color: var(--sm-text); font-size: 32rpx; line-height: 1.2; font-weight: 700; }
.round-action { display: grid; place-items: center; width: 61rpx; height: 61rpx; border: 1rpx solid var(--sm-border); border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text); -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
/* 编辑精选（弱化大卡）：紧凑横卡 */
.featured-card { display: flex; align-items: center; gap: 24rpx; margin-top: 20rpx; padding: 22rpx 24rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); box-shadow: 0 8rpx 22rpx var(--sm-art-shadow); }
.featured-thumb { flex: none; width: 156rpx; height: 156rpx; border-radius: 16rpx; overflow: hidden; background: var(--sm-surface-3); }
.featured-thumb .cover-art { width: 100%; height: 100%; border-radius: inherit; border: none; box-shadow: none; }
.featured-info { display: flex; flex: 1; min-width: 0; flex-direction: column; align-items: flex-start; gap: 10rpx; }
.feature-label { display: flex; align-items: center; gap: 8rpx; padding: 5rpx 13rpx; border-radius: 30rpx; background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 15rpx; font-weight: 700; letter-spacing: 1rpx; }
.featured-name { overflow: hidden; color: var(--sm-text); font-size: 27rpx; line-height: 1.25; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.featured-caption { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; line-height: 1.5; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.feature-play { display: flex; align-items: center; gap: 9rpx; margin-top: 4rpx; padding: 11rpx 20rpx; border-radius: 40rpx; background: var(--sm-hero-ink-bg); color: var(--sm-hero-ink); font-size: 17rpx; font-weight: 700; }
.feature-count { flex: none; display: flex; align-items: center; gap: 7rpx; padding: 6rpx 14rpx; border-radius: 30rpx; background: var(--sm-surface-3); color: var(--sm-text-2); font-size: 16rpx; }
/* 最近加入 · 友好空/错状态（不显示生硬错误，引导用户去逛精选） */
.recommend-empty { display: flex; flex-direction: column; align-items: flex-start; gap: 8rpx; padding: 30rpx 32rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface); }
.recommend-empty-title { color: var(--sm-text); font-size: 26rpx; font-weight: 700; }
.recommend-empty-sub { color: var(--sm-text-2); font-size: 18rpx; }
.collection-scroll { width: calc(100% + 40rpx); margin-top: 28rpx; }
.collection-list { display: flex; gap: 18rpx; padding-right: 40rpx; }
.collection-card { width: 220rpx; flex: none; }
.collection-cover { width: 220rpx; height: 220rpx; border-radius: 20rpx; overflow: hidden; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border); }
.collection-title { display: block; overflow: hidden; margin-top: 14rpx; color: var(--sm-text); font-size: 21rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.collection-meta { display: block; margin-top: 5rpx; color: var(--sm-text-2); font-size: 16rpx; }
/* 推荐歌单大卡（发现页首个歌单）：封面 + 推荐语 + 曲目数 */
.playlist-hero { display: flex; align-items: center; gap: 26rpx; margin-top: 26rpx; padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(var(--sm-blur)); backdrop-filter: blur(var(--sm-blur)); box-shadow: var(--sm-shadow-sm); transition: transform .2s ease; }
.playlist-hero:active { transform: scale(.985); }
.playlist-hero-cover { flex: none; width: 200rpx; height: 200rpx; }
.playlist-hero-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 10rpx; }
.playlist-hero-title { overflow: hidden; color: var(--sm-text); font-size: 28rpx; font-weight: 800; text-overflow: ellipsis; white-space: nowrap; }
.playlist-hero-desc { display: -webkit-box; overflow: hidden; color: var(--sm-text-2); font-size: 18rpx; line-height: 1.55; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.playlist-hero-meta { color: var(--sm-text-3); font-size: 16rpx; }
.mood-section { margin-top: 72rpx; }
.mood-scroll { width: calc(100% + 40rpx); margin-top: 28rpx; }
.mood-list { display: flex; gap: 13rpx; padding-right: 40rpx; }
.mood-chip { display: flex; flex: none; align-items: center; gap: 10rpx; height: 67rpx; padding: 0 22rpx; border: 1rpx solid var(--sm-border); border-radius: 36rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 20rpx; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); transition: all .2s ease; }
.mood-chip.selected { border-color: transparent; background: var(--sm-accent); color: var(--sm-accent-ink); font-weight: 700; }
.mood-dot { width: 13rpx; height: 13rpx; border-radius: 50%; background: var(--sm-accent); }
.mood-0 .mood-dot { background: var(--sm-accent); }.mood-1 .mood-dot { background: var(--sm-accent-2); }.mood-2 .mood-dot { background: var(--sm-accent-3); }.mood-3 .mood-dot { background: var(--sm-warn); }.mood-4 .mood-dot { background: var(--sm-danger); }
.recommend-section { margin-top: 72rpx; }
.recommend-heading { margin-bottom: 20rpx; }
.track-list { padding: 4rpx 0; }
.no-results, .inline-error { padding: 38rpx 0; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.request-link { display: flex; align-items: center; justify-content: center; gap: 8rpx; margin-top: 18rpx; color: var(--sm-accent); font-size: 18rpx; }
.bottom-spacer { height: 245rpx; }
.genre-grid { display: flex; flex-wrap: wrap; gap: 16rpx; margin-top: 23rpx; }
.genre-chip { display: flex; align-items: center; justify-content: space-between; width: calc(50% - 8rpx); height: 84rpx; padding: 0 24rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface); color: var(--sm-text); font-size: 21rpx; font-weight: 600; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.genre-chip :deep(svg), .genre-chip :deep(.lucide-shim) { color: var(--sm-text-3); }
.search-results { margin-top: 40rpx; }
.library-wrap { padding-top: 40rpx; }
.library-block { margin-top: 44rpx; }
.profile-card, .playlist-row, .request-banner, .login-card { display: flex; align-items: center; gap: 17rpx; margin-top: 12rpx; padding: 24rpx 26rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.profile-avatar { display: grid; flex: none; place-items: center; width: 80rpx; height: 80rpx; border-radius: 50%; background: var(--sm-avatar-bg); color: var(--sm-cover-ink); font-size: 28rpx; font-weight: 700; }
.profile-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 7rpx; }
.profile-name { color: var(--sm-text); font-size: 24rpx; font-weight: 700; }
.profile-email { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.profile-chevron { flex: none; color: var(--sm-text-3); }
.create-playlist { display: flex; align-items: center; gap: 6rpx; padding-bottom: 4rpx; color: var(--sm-accent); font-size: 19rpx; font-weight: 700; }
/* 新建歌单受控弹层：居中 Dialog + 半透明遮罩 */
.sheet-mask { position: fixed; inset: 0; z-index: 60; display: flex; align-items: center; justify-content: center; padding: 40rpx; background: var(--sm-mask); -webkit-backdrop-filter: blur(4px); backdrop-filter: blur(4px); }
.sheet-dialog { width: 100%; max-width: 560rpx; padding: 40rpx 36rpx 32rpx; border-radius: 28rpx; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); box-shadow: var(--sm-shadow); }
.sheet-title { display: block; color: var(--sm-text); font-size: 28rpx; font-weight: 800; }
.sheet-input { width: 100%; height: 88rpx; margin-top: 26rpx; padding: 0 24rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); color: var(--sm-text); font-size: 22rpx; box-sizing: border-box; }
.sheet-input::placeholder { color: var(--sm-text-3); }
.sheet-actions { display: flex; gap: 18rpx; margin-top: 32rpx; }
.sheet-btn { display: flex; flex: 1; align-items: center; justify-content: center; height: 82rpx; border-radius: 41rpx; font-size: 21rpx; font-weight: 700; }
.sheet-btn.ghost { border: 1rpx solid var(--sm-border); background: var(--sm-surface); color: var(--sm-text-2); }
.sheet-btn.primary { background: var(--sm-accent); color: var(--sm-accent-ink); }
.sheet-btn.disabled { opacity: .6; }
.playlist-icon { display: grid; flex: none; place-items: center; width: 66rpx; height: 66rpx; border-radius: 16rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.playlist-thumbnail { width: 100%; height: 100%; border-radius: inherit; }
.playlist-copy { display: flex; flex: 1; flex-direction: column; gap: 6rpx; }
.playlist-name { color: var(--sm-text); font-size: 21rpx; font-weight: 700; }
.playlist-meta { color: var(--sm-text-2); font-size: 16rpx; }
.library-count { padding-bottom: 4rpx; color: var(--sm-text-2); font-size: 18rpx; }
/* 曲库 tab：条目行（私有/已公开徽章 + 移除引用） */
.lib-head { padding: 6rpx 0 18rpx; }
.lib-count { color: var(--sm-text-2); font-size: 18rpx; }
.lib-row { display: flex; align-items: center; min-height: 106rpx; gap: 18rpx; border-bottom: 1rpx solid var(--sm-divider); }
.lib-row.is-removing { opacity: .5; pointer-events: none; }
.lib-cover { flex: none; width: 76rpx; height: 76rpx; border-radius: 14rpx; overflow: hidden; background: var(--sm-surface-3); }
.lib-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 7rpx; }
.lib-title-row { display: flex; align-items: center; gap: 12rpx; min-width: 0; }
.lib-title { overflow: hidden; color: var(--sm-text); font-size: 21rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.lib-badge { flex: none; padding: 4rpx 12rpx; border-radius: 20rpx; font-size: 13rpx; font-weight: 700; }
.lib-badge.private { border: 1rpx solid var(--sm-border); background: var(--sm-surface); color: var(--sm-text-2); }
.lib-badge.public { border: 1rpx solid var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }
.lib-artist { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.lib-meta { display: flex; flex: none; align-items: center; gap: 18rpx; }
.lib-duration { color: var(--sm-text-2); font-family: 'DM Sans', sans-serif; font-size: 16rpx; }
.lib-remove { display: grid; place-items: center; width: 44rpx; height: 42rpx; color: var(--sm-text-3); }
.empty-upload { display: flex; align-items: center; gap: 8rpx; margin-top: 24rpx; padding: 14rpx 32rpx; border-radius: var(--sm-radius-pill); background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 700; }
.empty-state { display: flex; flex-direction: column; align-items: center; padding: 70rpx 0 80rpx; }
.empty-icon { display: grid; place-items: center; width: 83rpx; height: 83rpx; border-radius: 50%; background: var(--sm-accent-soft); color: var(--sm-accent); }
.empty-title { margin-top: 20rpx; color: var(--sm-text); font-size: 23rpx; font-weight: 700; }
.empty-copy { margin-top: 9rpx; color: var(--sm-text-2); font-size: 18rpx; }
.login-card { flex-direction: column; align-items: flex-start; margin-top: 40rpx; padding: 40rpx 32rpx; }
.login-icon { display: grid; place-items: center; width: 72rpx; height: 72rpx; border-radius: 20rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.login-title { margin-top: 22rpx; color: var(--sm-text); font-size: 27rpx; font-weight: 800; }
.login-copy { margin-top: 12rpx; color: var(--sm-text-2); font-size: 18rpx; line-height: 1.6; }
.login-button { display: flex; align-items: center; justify-content: center; align-self: stretch; margin-top: 26rpx; height: 84rpx; border-radius: 42rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 21rpx; font-weight: 800; }
.request-banner { margin: 30rpx 0 0; }
.request-icon { display: grid; flex: none; place-items: center; width: 53rpx; height: 53rpx; border-radius: 17rpx; background: var(--sm-danger-soft); color: var(--sm-danger); }
.upload-copy { display: flex; flex: 1; flex-direction: column; gap: 6rpx; }
.upload-title { color: var(--sm-text); font-size: 19rpx; font-weight: 700; }
.upload-subtitle { color: var(--sm-text-2); font-size: 16rpx; }
/* 平台适配：固定底栏用 env(safe-area-inset-bottom) 避开 iPhone 底部安全区 */
.tabbar { position: fixed; z-index: 3; right: 0; bottom: 0; left: 0; display: flex; justify-content: space-around; padding: 17rpx 35rpx calc(env(safe-area-inset-bottom) + 20rpx); border-top: 1rpx solid var(--sm-surface-3); background: var(--sm-tabbar-bg); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.tab-item { display: flex; min-width: 105rpx; flex-direction: column; align-items: center; gap: 6rpx; padding: 10rpx 0; border-radius: 40rpx; color: var(--sm-text-3); font-size: 16rpx; transition: background .2s ease, color .2s ease; }
.tab-item.active { color: var(--sm-text); font-weight: 700; background: var(--sm-accent-soft); }
.tab-item :deep(svg), .tab-item :deep(.lucide-shim) { transition: transform .18s ease; }
.tab-item.active :deep(svg), .tab-item.active :deep(.lucide-shim) { color: var(--sm-accent); transform: translateY(-2rpx) scale(1.1); }
/* 中间凸起「消息」按钮：上浮出底栏顶边，未读角标贴右上角（描边用底栏底色做「嵌」进去的效果） */
.tab-fab { position: relative; align-self: flex-start; display: grid; place-items: center; width: 96rpx; height: 96rpx; margin-top: -35rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; background: var(--sm-accent-grad); color: var(--sm-accent-ink); box-shadow: 0 10rpx 26rpx var(--sm-accent-glow); box-sizing: border-box; }
.tab-fab-badge { position: absolute; top: -8rpx; right: -10rpx; min-width: 32rpx; padding: 0 8rpx; border: 2rpx solid var(--sm-tabbar-bg); border-radius: 20rpx; background: var(--sm-danger); color: #ffffff; font-size: 16rpx; font-weight: 800; line-height: 30rpx; text-align: center; box-sizing: border-box; }
/* 我的页：圆形快捷入口行（消息带未读角标） */
.quick-circles { display: flex; justify-content: space-between; margin: 4rpx 0 30rpx; padding: 26rpx 34rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(var(--sm-blur)); backdrop-filter: blur(var(--sm-blur)); }
.quick-circle { position: relative; display: flex; flex-direction: column; align-items: center; gap: 10rpx; }
.qc-icon { display: grid; place-items: center; width: 84rpx; height: 84rpx; border-radius: 50%; transition: transform .15s ease; }
.quick-circle:active .qc-icon { transform: scale(.92); }
.qc-notify { background: var(--sm-warn-soft); color: var(--sm-warn); }
.qc-favorite { background: var(--sm-danger-soft); color: var(--sm-danger); }
.qc-history { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.qc-follow { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
.qc-badge { position: absolute; top: -10rpx; right: -6rpx; min-width: 30rpx; padding: 0 8rpx; border: 2rpx solid var(--sm-bg); border-radius: 20rpx; background: var(--sm-danger); color: #ffffff; font-size: 15rpx; font-weight: 800; line-height: 28rpx; text-align: center; box-sizing: border-box; }
.qc-label { color: var(--sm-text-2); font-size: 17rpx; }
/* 我的页：分组入口列表（音乐库 / 设置） */
.entry-group { margin-bottom: 30rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(var(--sm-blur)); backdrop-filter: blur(var(--sm-blur)); overflow: hidden; }
.entry-group-title { display: block; padding: 20rpx 26rpx 4rpx; color: var(--sm-text-3); font-size: 17rpx; font-weight: 800; letter-spacing: .12em; }
.entry-row { display: flex; align-items: center; gap: 20rpx; padding: 22rpx 26rpx; border-top: 1rpx solid var(--sm-divider); transition: background .15s ease; }
.entry-row:active { background: var(--sm-surface-2); }
.entry-icon { display: grid; flex: none; place-items: center; width: 62rpx; height: 62rpx; border-radius: 18rpx; }
.ei-upload { background: var(--sm-accent-soft); color: var(--sm-accent); }
.ei-request { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.ei-theme { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
.ei-profile { background: var(--sm-warn-soft); color: var(--sm-warn); }
.entry-name { flex: 1; color: var(--sm-text); font-size: 21rpx; font-weight: 600; }
@media (min-width: 700px) {
  .app-shell { max-width: 720px; min-height: 100vh; margin: 0 auto; border-right: 1rpx solid var(--sm-divider); border-left: 1rpx solid var(--sm-divider); }
  .tabbar { right: max(0px, calc((100vw - 720px) / 2)); left: max(0px, calc((100vw - 720px) / 2)); }
}
</style>
