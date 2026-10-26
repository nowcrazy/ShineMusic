<script setup lang="ts">
/**
 * 歌单详情页：展示歌单信息与曲目，支持播放全部、添加曲目、重命名、删除歌单、移除单曲。
 * 进入参数（onLoad query）：id —— 歌单 ID（缺失时展示错误态）。
 * 对应后端接口：services/library 的 getPlaylist / renamePlaylist / deletePlaylist / removePlaylistTrack。
 */
import { ref } from 'vue'
import { onLoad, onUnload } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { ListMusic, Loader, Pencil, Play, Plus, Trash2, X } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import {
  deletePlaylist,
  getPlaylist,
  removePlaylistPendingTrack,
  removePlaylistTrack,
  renamePlaylist,
} from '@/services/library'
import { playQueue } from '@/services/player'
import { resolveUrl } from '@/services/http'
import type { ApiPlaylistDetail, ApiPlaylistPendingTrack, Track } from '@/types/music'

const { t } = useI18n()

// 页面状态：歌单 ID / 歌单详情 / 曲目列表 / 待补齐曲目 / 加载中 / 加载失败
const playlistId = ref('')
const detail = ref<ApiPlaylistDetail | null>(null)
const tracks = ref<Track[]>([])
const pendings = ref<ApiPlaylistPendingTrack[]>([])
const loading = ref(true)
const loadError = ref(false)

/** 列表行：正式曲目或待补齐条目（原歌单顺序合并渲染）。 */
type Row = { kind: 'track'; track: Track } | { kind: 'pending'; pending: ApiPlaylistPendingTrack }
const rows = ref<Row[]>([])

// 下载进行中的 pending 轮询定时器（8s）
let pollTimer: ReturnType<typeof setTimeout> | null = null

/** 按原歌单顺序合并正式曲目与 pending 行：pending.sortOrder 命中的槽位渲染 pending，否则顺序取下一首正式曲目。 */
function buildRows() {
  const pendingBySlot = new Map(pendings.value.map((pending) => [pending.sortOrder, pending]))
  const total = tracks.value.length + pendings.value.length
  const merged: Row[] = []
  let cursor = 0
  for (let slot = 0; slot < total; slot++) {
    const pending = pendingBySlot.get(slot)
    if (pending) merged.push({ kind: 'pending', pending })
    else if (cursor < tracks.value.length) merged.push({ kind: 'track', track: tracks.value[cursor++] })
  }
  // 兜底：槽位耗尽后仍有剩余正式曲目（pending 被删除等场景），追加到尾部。
  while (cursor < tracks.value.length) merged.push({ kind: 'track', track: tracks.value[cursor++] })
  rows.value = merged
}

/** 存在下载中/等待下载的 pending 时 8s 轮询刷新（发布完成后自动转正为可播放）。 */
function schedulePoll() {
  if (pollTimer) {
    clearTimeout(pollTimer)
    pollTimer = null
  }
  if (pendings.value.some((pending) => pending.status === 'pending' || pending.status === 'downloading')) {
    pollTimer = setTimeout(() => void load(false), 8000)
  }
}

onUnload(() => {
  if (pollTimer) clearTimeout(pollTimer)
})

/**
 * 解析 onLoad query：必须携带歌单 id，缺参进入错误态
 * @param options query，期望含 id
 */
onLoad((options) => {
  playlistId.value = options?.id ?? ''
  if (!playlistId.value) {
    loadError.value = true
    loading.value = false
    return
  }
  void load()
})

/**
 * 加载歌单详情与曲目，并以歌单名同步导航栏标题
 * @param showLoading 是否展示加载态（轮询刷新时为 false，避免闪烁）
 * 关键副作用：失败置 loadError；无论成败关闭 loading；合并 pending 行并按需排轮询
 */
async function load(showLoading = true) {
  if (showLoading) loading.value = true
  try {
    const result = await getPlaylist(playlistId.value)
    detail.value = result.detail
    tracks.value = result.tracks
    pendings.value = result.detail.pendingTracks ? [...result.detail.pendingTracks] : []
    buildRows()
    schedulePoll()
    uni.setNavigationBarTitle({ title: result.detail.name })
  } catch {
    if (showLoading) loadError.value = true
  } finally {
    loading.value = false
  }
}

/** 播放全部：歌单内曲目整体入队（空歌单不响应） */
function playAll() {
  if (tracks.value.length) playQueue(tracks.value)
}

/**
 * 重命名歌单：可编辑 Modal 弹窗，校验名称非空后提交并重新加载详情
 */
function handleRename() {
  uni.showModal({
    title: t('playlistPage.renameTitle'),
    editable: true,
    content: detail.value?.name,
    placeholderText: t('playlistPage.namePlaceholder'),
    success: async ({ confirm, content }) => {
      if (!confirm) return
      const name = (content ?? '').trim()
      if (!name) {
        uni.showToast({ title: t('playlistPage.nameRequired'), icon: 'none' })
        return
      }
      try {
        await renamePlaylist(playlistId.value, name)
        uni.showToast({ title: t('playlistPage.saved'), icon: 'none' })
        void load()
      } catch {
        uni.showToast({ title: t('playlistPage.saveFailed'), icon: 'none' })
      }
    },
  })
}

/**
 * 删除歌单：危险操作二次确认（红色确认按钮），成功后延迟返回上一页
 */
function handleDelete() {
  uni.showModal({
    title: t('playlistPage.deleteTitle'),
    content: t('playlistPage.deleteConfirm', { name: detail.value?.name ?? '' }),
    confirmText: t('playlistPage.delete'),
    confirmColor: '#d0553a',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deletePlaylist(playlistId.value)
        uni.showToast({ title: t('playlistPage.deleted'), icon: 'none' })
        setTimeout(() => uni.navigateBack(), 400)
      } catch {
        uni.showToast({ title: t('playlistPage.deleteFailed'), icon: 'none' })
      }
    },
  })
}

/** 跳转“挑选歌曲”页，并透传当前歌单 id */
function goPickTracks() {
  uni.navigateTo({ url: `/pages/playlist/pick-track?id=${playlistId.value}` })
}

/**
 * 移除歌单中的单曲
 * @param track 要移除的曲目
 * 关键副作用：接口成功后直接本地过滤掉该行（成功即更新，失败保留并 toast）
 */
async function handleRemove(track: Track) {
  try {
    await removePlaylistTrack(playlistId.value, track.id)
    tracks.value = tracks.value.filter((item) => item.id !== track.id) // 成功后本地移除
  } catch {
    uni.showToast({ title: t('playlistPage.removeFailed'), icon: 'none' })
  }
}

/** 待补齐行状态文案：downloading/pending/no_source/failed。 */
function pendingStatusText(status: string) {
  switch (status) {
    case 'downloading': return t('playlistPage.pendingDownloading')
    case 'pending': return t('playlistPage.pendingWaiting')
    case 'no_source': return t('playlistPage.pendingNoSource')
    default: return t('playlistPage.pendingFailed')
  }
}

/**
 * 移除歌单中的一条待补齐（pending）曲目
 * 关键副作用：接口成功后直接本地过滤掉该行（成功即更新，失败保留并 toast）
 */
async function handleRemovePending(pending: ApiPlaylistPendingTrack) {
  try {
    await removePlaylistPendingTrack(playlistId.value, pending.id)
    pendings.value = pendings.value.filter((item) => item.id !== pending.id)
    buildRows()
  } catch {
    uni.showToast({ title: t('playlistPage.removeFailed'), icon: 'none' })
  }
}
</script>

<template>
  <scroll-view class="detail-page" scroll-y :show-scrollbar="false">
    <!-- 加载中 / 失败或不存在 的状态提示 -->
    <view v-if="loading" class="state-text">{{ t('playlistPage.loading') }}</view>
    <view v-else-if="loadError || !detail" class="state-text">{{ t('playlistPage.notFound') }}</view>
    <template v-else>
      <!-- 歌单头部：封面、名称、简介与操作组（播放/添加/重命名/删除） -->
      <view class="detail-header">
        <view class="playlist-art">
          <image v-if="detail.artworkUrl" class="playlist-image" :src="resolveUrl(detail.artworkUrl)" mode="aspectFill" />
          <ListMusic v-else :size="46" />
        </view>
        <text class="detail-title">{{ detail.name }}</text>
        <text v-if="detail.description" class="detail-sub">{{ detail.description }}</text>
        <text class="detail-meta">{{ t('catalog.trackCount', { count: rows.length }) }}</text>
        <view class="header-actions">
          <view class="play-all" @tap="playAll"><Play :size="16" fill="currentColor" /><text>{{ t('playlistPage.playAll') }}</text></view>
          <view class="icon-action" @tap="goPickTracks"><Plus :size="20" /></view>
          <view class="icon-action" @tap="handleRename"><Pencil :size="18" /></view>
          <view class="icon-action danger" @tap="handleDelete"><Trash2 :size="18" /></view>
        </view>
      </view>
      <!-- 曲目列表：正式曲目与 pending 待补齐行按原歌单顺序合并；空歌单展示空态与添加入口 -->
      <view class="detail-list">
        <template v-for="(row, index) in rows" :key="row.kind === 'track' ? row.track.id : row.pending.id">
          <view v-if="row.kind === 'track'" class="removable-row">
            <view class="row-main">
              <TrackRow :track="row.track" :index="index" :queue="tracks" />
            </view>
            <view class="remove-button" @tap.stop="handleRemove(row.track)"><X :size="15" /></view>
          </view>
          <!-- pending 行：灰色不可播放，展示下载状态；发布完成后轮询自动转正 -->
          <view v-else class="removable-row">
            <view class="row-main">
              <view class="pending-row">
                <text class="pending-index">{{ index + 1 }}</text>
                <view class="pending-copy">
                  <text class="pending-title">{{ row.pending.title }}</text>
                  <text class="pending-artist">{{ row.pending.artists.join(' / ') || '-' }}</text>
                </view>
                <view class="pending-status">
                  <Loader v-if="row.pending.status === 'downloading'" :size="12" class="spin" />
                  <text>{{ pendingStatusText(row.pending.status) }}</text>
                </view>
              </view>
            </view>
            <view class="remove-button" @tap.stop="handleRemovePending(row.pending)"><X :size="15" /></view>
          </view>
        </template>
        <view v-if="!rows.length" class="empty-tip">
          <text>{{ t('playlistPage.empty') }}</text>
          <view class="add-button" @tap="goPickTracks"><Plus :size="15" /><text>{{ t('playlistPage.addTracks') }}</text></view>
        </view>
      </view>
      <view class="page-bottom-spacer"></view>
    </template>
  </scroll-view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 歌单详情（方向 B · 玻璃质感） */
.detail-page { height: 100vh; padding: 32rpx 40rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.detail-header { display: flex; flex-direction: column; align-items: flex-start; padding: 12rpx 0 20rpx; }
.playlist-art { display: grid; place-items: center; align-self: center; width: 260rpx; height: 260rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 26rpx; background: var(--sm-cover-bg); color: var(--sm-cover-ink); box-shadow: 0 18rpx 44rpx var(--sm-art-shadow); }
.playlist-image { width: 100%; height: 100%; border-radius: inherit; }
.detail-title { margin-top: 28rpx; color: var(--sm-text); font-size: 38rpx; font-weight: 800; }
.detail-sub { margin-top: 12rpx; color: var(--sm-text-2); font-size: 20rpx; line-height: 1.6; }
.detail-meta { margin-top: 14rpx; color: var(--sm-text-2); font-size: 17rpx; }
.header-actions { display: flex; align-items: center; gap: 16rpx; margin-top: 26rpx; }
.play-all { display: flex; align-items: center; gap: 10rpx; padding: 16rpx 34rpx; border-radius: 44rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 20rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.icon-action { display: grid; place-items: center; width: 72rpx; height: 72rpx; border: 1rpx solid var(--sm-border); border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text-2); -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.icon-action.danger { color: var(--sm-danger); }
.detail-list { margin-top: 20rpx; }
.removable-row { display: flex; align-items: center; gap: 8rpx; }
.row-main { flex: 1; min-width: 0; }
.remove-button { display: grid; flex: none; place-items: center; width: 44rpx; height: 44rpx; border-radius: 50%; background: var(--sm-danger-soft); color: var(--sm-danger); }
/* pending 待补齐行：整体降透明度表示灰色不可播放 */
.pending-row { display: flex; align-items: center; gap: 18rpx; padding: 20rpx 0; opacity: 0.45; }
.pending-index { flex: none; width: 44rpx; color: var(--sm-text-3); font-size: 18rpx; text-align: center; }
.pending-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.pending-title { overflow: hidden; color: var(--sm-text); font-size: 21rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.pending-artist { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.pending-status { display: flex; flex: none; align-items: center; gap: 6rpx; color: var(--sm-text-3); font-size: 16rpx; }
.spin { animation: sm-spin 1s linear infinite; }
@keyframes sm-spin { to { transform: rotate(360deg); } }
.empty-tip { display: flex; flex-direction: column; align-items: center; gap: 22rpx; padding: 70rpx 0; color: var(--sm-text-2); font-size: 19rpx; }
.add-button { display: flex; align-items: center; gap: 8rpx; padding: 14rpx 28rpx; border-radius: 40rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 700; }
.page-bottom-spacer { height: 180rpx; }
</style>
