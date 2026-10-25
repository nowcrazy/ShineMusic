<script setup lang="ts">
/**
 * 歌单添加曲目页：搜索曲库并把选中曲目加入指定歌单；点击结果行可试听。
 * 进入参数（onLoad query）：id —— 目标歌单 ID。
 * 对应后端接口：services/catalog.searchTracks（防抖搜索）、services/library.addPlaylistTrack（加入歌单）。
 */
import { ref, watch } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Check, Plus, Search } from 'lucide-vue-next'
import { searchTracks } from '@/services/catalog'
import { addPlaylistTrack } from '@/services/library'
import { playTrack } from '@/services/player'
import { formatTime } from '@/utils/format'
import type { Track } from '@/types/music'

const { t } = useI18n()

// 页面状态：目标歌单 ID / 搜索词 / 结果 / 请求中 / 是否已搜索 / 本次会话已加入的曲目 ID 集合 / 防抖计时器
const playlistId = ref('')
const query = ref('')
const results = ref<Track[]>([])
const pending = ref(false)
const searched = ref(false)
const addedIds = ref(new Set<string>())
let timer: ReturnType<typeof setTimeout> | undefined

/** 解析 onLoad query 获取目标歌单 id */
onLoad((options) => {
  playlistId.value = options?.id ?? ''
})

/**
 * 监听搜索词：280ms 防抖请求；清空关键词立即重置结果与已搜索标记
 */
watch(query, (value) => {
  if (timer) clearTimeout(timer)
  const keyword = value.trim()
  if (!keyword) {
    results.value = []
    searched.value = false
    return
  }
  pending.value = true
  // 防抖：停顿 280ms 后发起搜索
  timer = setTimeout(async () => {
    try {
      results.value = await searchTracks(keyword)
    } catch {
      results.value = []
    } finally {
      pending.value = false
      searched.value = true
    }
  }, 280)
})

/**
 * 将曲目加入目标歌单
 * @param track 选中的曲目
 * 关键副作用：成功后把 id 写入 addedIds（按钮切换为“已加入”，属本次会话内的本地乐观标记）；
 * 登录过期单独提示重新登录
 */
async function handleAdd(track: Track) {
  try {
    await addPlaylistTrack(playlistId.value, track.id)
    // 复制新 Set 触发响应式更新（直接 add 不会触发视图刷新）
    const next = new Set(addedIds.value)
    next.add(track.id)
    addedIds.value = next
    uni.showToast({ title: t('playlistPage.added'), icon: 'none' })
  } catch (error) {
    const code = (error as { code?: string }).code
    uni.showToast({
      title: code === 'authentication_required' ? t('playlistPage.sessionExpired') : t('playlistPage.addFailed'),
      icon: 'none',
    })
  }
}
</script>

<template>
  <view class="pick-page">
    <!-- 搜索框：自动聚焦，输入防抖触发搜索 -->
    <view class="search-box">
      <Search :size="19" :stroke-width="2" />
      <input v-model="query" focus confirm-type="search" :placeholder="t('playlistPage.searchPlaceholder')" />
    </view>

    <!-- 状态提示：搜索中 / 无结果 / 未输入 -->
    <view v-if="pending" class="state-text">{{ t('playlistPage.searching') }}</view>
    <view v-else-if="searched && !results.length" class="state-text">{{ t('playlistPage.noMatch') }}</view>
    <view v-else-if="!searched" class="state-text">{{ t('playlistPage.searchHint') }}</view>

    <!-- 搜索结果行：点击封面/信息试听，右侧圆形按钮加入歌单（@click.stop 不触发试听） -->
    <view v-for="track in results" :key="track.id" class="pick-row">
      <img class="pick-cover" :src="track.cover" mode="aspectFill" @click="playTrack(track, results)" />
      <view class="pick-copy" @click="playTrack(track, results)">
        <text class="pick-title">{{ track.title }}</text>
        <text class="pick-artist">{{ track.artist }} · {{ track.album || t('playlistPage.unknownAlbum') }}</text>
      </view>
      <text class="pick-duration">{{ formatTime(track.duration) }}</text>
      <view
        class="pick-add"
        :class="{ added: addedIds.has(track.id) }"
        @click.stop="handleAdd(track)"
      >
        <Check v-if="addedIds.has(track.id)" :size="17" />
        <Plus v-else :size="17" />
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 添加曲目到歌单（方向 B · 玻璃质感） */
.pick-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.search-box { display: flex; align-items: center; gap: 18rpx; height: 90rpx; padding: 0 25rpx; border: 1rpx solid var(--sm-border); border-radius: 23rpx; background: var(--sm-surface); color: var(--sm-text-2); -webkit-backdrop-filter: blur(14px); backdrop-filter: blur(14px); }
.search-box input { flex: 1; min-width: 0; height: 100%; color: var(--sm-text); font-size: 23rpx; }
.search-box input::placeholder { color: var(--sm-text-3); }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.pick-row { display: flex; align-items: center; min-height: 106rpx; gap: 16rpx; border-bottom: 1rpx solid var(--sm-divider); }
.pick-cover { flex: none; width: 76rpx; height: 76rpx; border-radius: 14rpx; background: var(--sm-surface-3); }
.pick-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 7rpx; }
.pick-title { overflow: hidden; color: var(--sm-text); font-size: 21rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.pick-artist { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.pick-duration { width: 60rpx; color: var(--sm-text-2); font-size: 16rpx; text-align: right; }
.pick-add { display: grid; flex: none; place-items: center; width: 56rpx; height: 56rpx; border-radius: 50%; background: var(--sm-accent); color: var(--sm-accent-ink); }
.pick-add.added { background: var(--sm-surface-3); color: var(--sm-text-2); }
</style>
