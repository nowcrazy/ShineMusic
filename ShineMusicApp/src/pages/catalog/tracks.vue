<script setup lang="ts">
/**
 * 通用曲目列表页：按条件展示曲目并支持触底分页加载。
 * 进入参数（onLoad query，均可选）：
 * - genre：风格名（同时作为导航栏标题，title 可覆盖）；title：导航栏标题；
 * - artistId / albumId：按艺人或专辑过滤；sort：title | duration | recent。
 * 对应后端接口：services/catalog.getTracks（分页查询，每页 pageSize=20）。
 */
import { ref } from 'vue'
import { onLoad, onReachBottom } from '@dcloudio/uni-app'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getTracks, type TrackQuery } from '@/services/catalog'
import type { Track } from '@/types/music'

// 列表与分页状态：当前曲目 / 首屏加载 / 加载更多 / 失败态 / 当前页码 / 每页条数 / 总条数 / 查询条件
const tracks = ref<readonly Track[]>([])
const loading = ref(true)
const loadingMore = ref(false)
const loadError = ref(false)
const page = ref(1)
const pageSize = 20
const total = ref(0)
const query = ref<TrackQuery>({ page: 1, pageSize })

/**
 * 解析 onLoad query 组装查询条件并设置导航栏标题，随后加载第一页
 * @param options query：genre / title / artistId / albumId / sort
 */
onLoad((options) => {
  if (options?.genre) {
    query.value.genre = decodeURIComponent(options.genre)
    uni.setNavigationBarTitle({ title: query.value.genre })
  }
  if (options?.title) uni.setNavigationBarTitle({ title: decodeURIComponent(options.title) })
  if (options?.artistId) query.value.artistId = options.artistId
  if (options?.albumId) query.value.albumId = options.albumId
  if (options?.sort === 'title' || options?.sort === 'duration' || options?.sort === 'recent') {
    query.value.sort = options.sort
  }
  void load()
})

/**
 * 加载第一页：用 query 条件请求并覆盖列表
 * 关键副作用：失败置 loadError；结束后关闭首屏 loading
 */
async function load() {
  try {
    const result = await getTracks(query.value)
    tracks.value = result.items
    total.value = result.total
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

/**
 * 触底分页加载：正在加载或已加载全部时跳过；页码 +1 后请求下一页并追加
 * 关键副作用：请求失败时回滚页码（下次触底可重试同一页）
 */
onReachBottom(async () => {
  if (loadingMore.value || tracks.value.length >= total.value) return
  loadingMore.value = true
  try {
    page.value += 1
    const result = await getTracks({ ...query.value, page: page.value })
    tracks.value = [...tracks.value, ...result.items] // 追加下一页结果
  } catch {
    page.value -= 1 // 失败回滚页码
  } finally {
    loadingMore.value = false
  }
})
</script>

<template>
  <view class="list-page">
    <!-- 加载中 / 失败状态 -->
    <view v-if="loading" class="state-text">正在加载…</view>
    <view v-else-if="loadError" class="state-text">暂时无法打开曲目列表。</view>
    <template v-else>
      <!-- 列表头部：总数 -->
      <view class="list-head">
        <text class="list-count">共 {{ total }} 首</text>
      </view>
      <TrackRow
        v-for="(track, index) in tracks"
        :key="track.id"
        :track="track"
        :index="index"
        :queue="tracks"
      />
      <!-- 列表尾部：空态 / 已全部加载 / 正在加载下一页 -->
      <view v-if="!tracks.length" class="state-text">这里还没有曲目。</view>
      <view v-else-if="tracks.length >= total" class="list-end">已经到底了</view>
      <view v-else-if="loadingMore" class="list-end">加载中…</view>
    </template>
    <view class="page-bottom-spacer"></view>
  </view>

  <!-- 全局播放条 -->
  <PlayerBar />
</template>

<style lang="scss" scoped>
/* 曲目列表（方向 B · 玻璃质感） */
.list-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.list-head { padding: 6rpx 0 18rpx; }
.list-count { color: var(--sm-text-2); font-size: 18rpx; }
.list-end { padding: 36rpx 0 20rpx; color: var(--sm-text-3); font-size: 17rpx; text-align: center; }
.page-bottom-spacer { height: 180rpx; }
</style>
