<script setup lang="ts">
/**
 * 歌单详情页：展示歌单信息与曲目，支持播放全部、添加曲目、重命名、删除歌单、移除单曲。
 * 进入参数（onLoad query）：id —— 歌单 ID（缺失时展示错误态）。
 * 对应后端接口：services/library 的 getPlaylist / renamePlaylist / deletePlaylist / removePlaylistTrack。
 */
import { ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { ListMusic, Pencil, Play, Plus, Trash2, X } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import {
  deletePlaylist,
  getPlaylist,
  removePlaylistTrack,
  renamePlaylist,
} from '@/services/library'
import { playQueue } from '@/services/player'
import { resolveUrl } from '@/services/http'
import type { ApiPlaylistDetail, Track } from '@/types/music'

// 页面状态：歌单 ID / 歌单详情 / 曲目列表 / 加载中 / 加载失败
const playlistId = ref('')
const detail = ref<ApiPlaylistDetail | null>(null)
const tracks = ref<Track[]>([])
const loading = ref(true)
const loadError = ref(false)

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
 * 关键副作用：失败置 loadError；无论成败关闭 loading
 */
async function load() {
  try {
    const result = await getPlaylist(playlistId.value)
    detail.value = result.detail
    tracks.value = result.tracks
    uni.setNavigationBarTitle({ title: result.detail.name })
  } catch {
    loadError.value = true
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
    title: '修改歌单',
    editable: true,
    content: detail.value?.name,
    placeholderText: '歌单名称',
    success: async ({ confirm, content }) => {
      if (!confirm) return
      const name = (content ?? '').trim()
      if (!name) {
        uni.showToast({ title: '名字不能为空', icon: 'none' })
        return
      }
      try {
        await renamePlaylist(playlistId.value, name)
        uni.showToast({ title: '已保存', icon: 'none' })
        void load()
      } catch {
        uni.showToast({ title: '保存失败', icon: 'none' })
      }
    },
  })
}

/**
 * 删除歌单：危险操作二次确认（红色确认按钮），成功后延迟返回上一页
 */
function handleDelete() {
  uni.showModal({
    title: '删除歌单',
    content: `确定删除“${detail.value?.name ?? ''}”吗？此操作不可恢复。`,
    confirmText: '删除',
    confirmColor: '#d0553a',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deletePlaylist(playlistId.value)
        uni.showToast({ title: '歌单已删除', icon: 'none' })
        setTimeout(() => uni.navigateBack(), 400)
      } catch {
        uni.showToast({ title: '删除失败', icon: 'none' })
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
    uni.showToast({ title: '移除失败', icon: 'none' })
  }
}
</script>

<template>
  <scroll-view class="detail-page" scroll-y :show-scrollbar="false">
    <!-- 加载中 / 失败或不存在 的状态提示 -->
    <view v-if="loading" class="state-text">正在加载…</view>
    <view v-else-if="loadError || !detail" class="state-text">歌单不存在或暂时无法打开。</view>
    <template v-else>
      <!-- 歌单头部：封面、名称、简介与操作组（播放/添加/重命名/删除） -->
      <view class="detail-header">
        <view class="playlist-art">
          <image v-if="detail.artworkUrl" class="playlist-image" :src="resolveUrl(detail.artworkUrl)" mode="aspectFill" />
          <ListMusic v-else :size="46" />
        </view>
        <text class="detail-title">{{ detail.name }}</text>
        <text v-if="detail.description" class="detail-sub">{{ detail.description }}</text>
        <text class="detail-meta">{{ tracks.length }} 首</text>
        <view class="header-actions">
          <view class="play-all" @tap="playAll"><Play :size="16" fill="currentColor" /><text>播放全部</text></view>
          <view class="icon-action" @tap="goPickTracks"><Plus :size="20" /></view>
          <view class="icon-action" @tap="handleRename"><Pencil :size="18" /></view>
          <view class="icon-action danger" @tap="handleDelete"><Trash2 :size="18" /></view>
        </view>
      </view>
      <!-- 曲目列表：每行带移除按钮；空歌单展示空态与添加入口 -->
      <view class="detail-list">
        <view v-for="(track, index) in tracks" :key="track.id" class="removable-row">
          <view class="row-main">
            <TrackRow :track="track" :index="index" :queue="tracks" />
          </view>
          <view class="remove-button" @tap.stop="handleRemove(track)"><X :size="15" /></view>
        </view>
        <view v-if="!tracks.length" class="empty-tip">
          <text>歌单还是空的</text>
          <view class="add-button" @tap="goPickTracks"><Plus :size="15" /><text>添加歌曲</text></view>
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
.empty-tip { display: flex; flex-direction: column; align-items: center; gap: 22rpx; padding: 70rpx 0; color: var(--sm-text-2); font-size: 19rpx; }
.add-button { display: flex; align-items: center; gap: 8rpx; padding: 14rpx 28rpx; border-radius: 40rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 700; }
.page-bottom-spacer { height: 180rpx; }
</style>
