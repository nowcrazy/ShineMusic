<script setup lang="ts">
/**
 * 歌单管理页（管理员）：运营歌单（推荐歌单）的创建、信息编辑、删除与曲目编排。
 * 进入方式：首页头像抽屉 →「歌单管理」（仅管理员可见）。
 * 对应后端接口：services/admin-collections（POST/PUT/DELETE /admin/collections、
 *                PUT /admin/collections/{id}/tracks）；
 *                曲目检索复用 services/catalog.searchTracks。
 * 页面结构：列表态（全部歌单卡片）与编辑态（表单 + 曲目编排）互斥切换。
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { ArrowDown, ArrowUp, ChevronLeft, ListMusic, Plus, Search, Trash2, X } from 'lucide-vue-next'
import { isAdmin } from '@/services/session'
import {
  createCollection,
  deleteCollection,
  replaceCollectionTracks,
  searchTracksForCollection,
  updateCollection,
} from '@/services/admin-collections'
import { getCollection, getCollections } from '@/services/catalog'
import { resolveUrl } from '@/services/http'
import type { ApiCollectionSummary, ApiTrack } from '@/types/music'

/** 歌单编辑态内缓存的曲目摘要：仅展示所需字段（曲目检索返回 ApiTrack，详情返回 Track，两者统一为此最小结构） */
type CachedTrack = {
  id: string
  title: string
  artists: string
  artworkUrl: string | null
}

// —— 列表态 ——
const collections = ref<readonly ApiCollectionSummary[]>([])
const loading = ref(false)
const listMessage = ref('')

// —— 编辑态 ——
// 编辑中的歌单：null=新建，非 null=修改
type EditingState = {
  id: string | null
  title: string
  description: string
  trackIds: string[]
  trackCache: Map<string, CachedTrack> // 已选曲目的摘要缓存（用于列表展示）
}
const editing = ref<EditingState | null>(null)
const saving = ref(false)
const editMessage = ref('')

// —— 添加曲目搜索面板 ——
const addPanelOpen = ref(false)
const addQuery = ref('')
const addResults = ref<readonly ApiTrack[]>([])
const addSearching = ref(false)
const addMessage = ref('')

// 非管理员直接提示（服务端同样独立鉴权，此处仅前端防御）
if (!isAdmin.value) {
  uni.showToast({ title: '需要管理员权限', icon: 'none' })
}

/** 歌单封面地址解析（后端返回相对路径 artworkUrl） */
function coverUrl(collection: ApiCollectionSummary): string {
  return collection.artworkUrl ? resolveUrl(collection.artworkUrl) : ''
}

/** 返回上一页（无上一页时回首页） */
function goBack() {
  const pages = getCurrentPages()
  if (pages.length > 1) {
    uni.navigateBack()
  } else {
    uni.reLaunch({ url: '/pages/home/index' })
  }
}

/** 加载全部歌单（后端单次返回上限 50，运营歌单数量可控） */
async function loadCollections() {
  loading.value = true
  listMessage.value = ''
  try {
    collections.value = await getCollections()
  } catch (error) {
    listMessage.value = error instanceof Error ? error.message : '加载失败'
  } finally {
    loading.value = false
  }
}

onShow(() => {
  if (isAdmin.value) void loadCollections()
})

/** 打开新建歌单编辑器 */
function openCreate() {
  editing.value = { id: null, title: '', description: '', trackIds: [], trackCache: new Map() }
  editMessage.value = ''
  addPanelOpen.value = false
}

/** 打开已有歌单编辑器：拉取详情以装载曲目 */
async function openEdit(collection: ApiCollectionSummary) {
  editMessage.value = ''
  try {
    const detail = await getCollection(collection.id)
    const trackCache = new Map<string, CachedTrack>()
    for (const track of detail.tracks) trackCache.set(track.id, { id: track.id, title: track.title, artists: track.artist, artworkUrl: track.cover })
    editing.value = {
      id: collection.id,
      title: detail.detail.title,
      description: detail.detail.description ?? '',
      trackIds: detail.tracks.map((track) => track.id),
      trackCache,
    }
    addPanelOpen.value = false
  } catch (error) {
    uni.showToast({ title: error instanceof Error ? error.message : '加载歌单详情失败', icon: 'none' })
  }
}

/** 关闭编辑器回到列表 */
function closeEditor() {
  editing.value = null
  void loadCollections()
}

/** 保存歌单（新建或修改 + 曲目整体替换） */
async function handleSave() {
  if (!editing.value) return
  const state = editing.value
  const title = state.title.trim()
  if (!title) {
    editMessage.value = '歌单标题不能为空'
    return
  }
  saving.value = true
  editMessage.value = ''
  try {
    if (state.id === null) {
      await createCollection({
        title,
        description: state.description.trim() || null,
        trackIds: state.trackIds,
      })
    } else {
      await updateCollection(state.id, {
        title,
        description: state.description.trim() || null,
      })
      await replaceCollectionTracks(state.id, state.trackIds)
    }
    uni.showToast({ title: '歌单已保存', icon: 'none' })
    closeEditor()
  } catch (error) {
    editMessage.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    saving.value = false
  }
}

/** 删除歌单（二次确认） */
function handleDelete(collection: ApiCollectionSummary) {
  uni.showModal({
    title: '删除歌单',
    content: `确定删除「${collection.title}」？歌单内的曲目不会从曲库中移除。`,
    confirmText: '删除',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deleteCollection(collection.id)
        uni.showToast({ title: '已删除', icon: 'none' })
        void loadCollections()
      } catch (error) {
        uni.showToast({ title: error instanceof Error ? error.message : '删除失败', icon: 'none' })
      }
    },
  })
}

// —— 编辑器：曲目编排 ——

/** 从编辑器移除一首曲目 */
function removeTrack(trackId: string) {
  if (!editing.value) return
  editing.value.trackIds = editing.value.trackIds.filter((id) => id !== trackId)
}

/** 上移/下移曲目（调整歌单内顺序） */
function moveTrack(trackId: string, direction: -1 | 1) {
  if (!editing.value) return
  const ids = [...editing.value.trackIds]
  const index = ids.indexOf(trackId)
  const target = index + direction
  if (index < 0 || target < 0 || target >= ids.length) return
  ;[ids[index], ids[target]] = [ids[target], ids[index]]
  editing.value.trackIds = ids
}

/** 打开添加曲目面板并预载热门曲目 */
async function openAddPanel() {
  addPanelOpen.value = true
  addQuery.value = ''
  addResults.value = []
  addMessage.value = ''
  await runAddSearch('')
}

/** 执行曲目搜索（空关键词时返回最近加入的曲目） */
async function runAddSearch(query: string) {
  addSearching.value = true
  addMessage.value = ''
  try {
    const page = await searchTracksForCollection(query, 1, 50)
    addResults.value = page.items
  } catch (error) {
    addMessage.value = error instanceof Error ? error.message : '搜索失败'
  } finally {
    addSearching.value = false
  }
}

/** 把搜索结果中的曲目加入歌单（去重，最多 200 首） */
function addTrackToPlaylist(track: CachedTrack) {
  if (!editing.value) return
  if (editing.value.trackIds.includes(track.id)) {
    uni.showToast({ title: '该曲目已在歌单中', icon: 'none' })
    return
  }
  if (editing.value.trackIds.length >= 200) {
    uni.showToast({ title: '单个歌单最多 200 首', icon: 'none' })
    return
  }
  editing.value.trackIds.push(track.id)
  // 曲目摘要入缓存（最小可用信息，供编辑态列表展示）
  editing.value.trackCache.set(track.id, track)
  uni.showToast({ title: '已加入', icon: 'none' })
}

/** 编辑器内曲目展示信息（从缓存取，取不到回退占位） */
function trackInfo(trackId: string): CachedTrack {
  const track = editing.value?.trackCache.get(trackId)
  if (!track) return { id: trackId, title: trackId, artists: '', artworkUrl: null }
  return track
}

/** 搜索面板结果行的曲目信息 */
const addTrackInfo = computed(() =>
  addResults.value.map((track) => ({
    id: track.id,
    title: track.title,
    artists: track.artists.map((artist) => artist.name).join('、') || '未知艺人',
    artworkUrl: track.artworkUrl ?? null,
  })),
)
</script>

<template>
  <view class="manage-page">
    <!-- 顶部返回栏 -->
    <view class="page-head">
      <view class="back-button" @tap="editing ? closeEditor() : goBack()">
        <ChevronLeft v-if="!editing" :size="20" />
        <X v-else :size="20" />
      </view>
      <text class="page-title">{{ editing ? '编辑歌单' : '歌单管理' }}</text>
      <view v-if="!editing" class="head-action" @tap="openCreate"><Plus :size="18" /><text>新建</text></view>
    </view>

    <!-- ============ 列表态 ============ -->
    <scroll-view v-if="!editing" class="page-body" scroll-y :show-scrollbar="false">
      <view v-if="loading" class="inline-hint">加载中…</view>
      <view v-else-if="listMessage" class="inline-hint">{{ listMessage }}</view>
      <view v-else-if="!collections.length" class="empty-block">
        <ListMusic :size="26" />
        <text>还没有运营歌单</text>
        <text class="empty-sub">创建后会出现在首页「发现」页的推荐歌单区</text>
      </view>

      <view v-for="collection in collections" :key="collection.id" class="collection-row">
        <view class="collection-cover">
          <image v-if="coverUrl(collection)" class="cover-img" :src="coverUrl(collection)" mode="aspectFill" />
          <ListMusic v-else :size="20" />
        </view>
        <view class="collection-copy" @tap="openEdit(collection)">
          <text class="collection-title">{{ collection.title }}</text>
          <text class="collection-desc">{{ collection.description || '暂无推荐语' }}</text>
          <text class="collection-count">{{ collection.trackCount }} 首</text>
        </view>
        <view class="row-actions">
          <view class="row-action" @tap="openEdit(collection)"><Search :size="16" /></view>
          <view class="row-action danger" @tap="handleDelete(collection)"><Trash2 :size="16" /></view>
        </view>
      </view>
      <view class="bottom-space"></view>
    </scroll-view>

    <!-- ============ 编辑态 ============ -->
    <scroll-view v-else class="page-body" scroll-y :show-scrollbar="false">
      <view class="form-block">
        <text class="field-label">歌单标题</text>
        <input v-model="editing.title" class="field-input" type="text" placeholder="如：抖音热播" maxlength="100" />
        <text class="field-label">推荐语</text>
        <textarea v-model="editing.description" class="field-textarea" placeholder="一句话描述这个歌单（可选）" maxlength="500" />
      </view>

      <!-- 曲目编排 -->
      <view class="form-block">
        <view class="block-head">
          <text class="field-label">歌单曲目（{{ editing.trackIds.length }} 首，顺序即播放顺序）</text>
          <view class="add-track-button" @tap="openAddPanel"><Plus :size="15" /><text>添加曲目</text></view>
        </view>
        <view v-if="!editing.trackIds.length" class="empty-sub">还没有曲目，点击「添加曲目」从曲库挑选</view>
        <view v-for="(trackId, index) in editing.trackIds" :key="trackId" class="track-row">
          <text class="track-index">{{ index + 1 }}</text>
          <view class="track-copy">
            <text class="track-title">{{ trackInfo(trackId).title }}</text>
            <text class="track-artists">{{ trackInfo(trackId).artists }}</text>
          </view>
          <view class="track-actions">
            <view class="track-action" @tap="moveTrack(trackId, -1)"><ArrowUp :size="14" /></view>
            <view class="track-action" @tap="moveTrack(trackId, 1)"><ArrowDown :size="14" /></view>
            <view class="track-action danger" @tap="removeTrack(trackId)"><Trash2 :size="14" /></view>
          </view>
        </view>
      </view>

      <view class="save-bar">
        <text v-if="editMessage" class="field-message">{{ editMessage }}</text>
        <view class="save-button" :class="{ disabled: saving }" @tap="handleSave">
          <text>{{ saving ? '保存中…' : '保存歌单' }}</text>
        </view>
      </view>
      <view class="bottom-space"></view>
    </scroll-view>

    <!-- ============ 添加曲目面板（编辑态内弹出） ============ -->
    <view v-if="editing && addPanelOpen" class="add-panel">
      <view class="add-panel-mask" @tap="addPanelOpen = false"></view>
      <view class="add-panel-body">
        <view class="add-panel-head">
          <text class="add-panel-title">添加曲目</text>
          <view class="add-panel-close" @tap="addPanelOpen = false"><X :size="18" /></view>
        </view>
        <view class="add-search">
          <input
            v-model="addQuery"
            class="add-search-input"
            type="text"
            placeholder="搜索歌曲、艺人（留空显示全部）"
            confirm-type="search"
            @confirm="runAddSearch(addQuery)"
          />
          <view class="add-search-action" @tap="runAddSearch(addQuery)"><Search :size="16" /></view>
        </view>
        <scroll-view class="add-results" scroll-y :show-scrollbar="false">
          <view v-if="addSearching" class="inline-hint">搜索中…</view>
          <view v-else-if="addMessage" class="inline-hint">{{ addMessage }}</view>
          <view v-else-if="!addTrackInfo.length" class="inline-hint">没有找到曲目</view>
          <view
            v-for="track in addTrackInfo"
            :key="track.id"
            class="add-result-row"
            :class="{ added: editing.trackIds.includes(track.id) }"
            @tap="addTrackToPlaylist(track)"
          >
            <view class="track-copy">
              <text class="track-title">{{ track.title }}</text>
              <text class="track-artists">{{ track.artists }}</text>
            </view>
            <Plus v-if="!editing.trackIds.includes(track.id)" :size="16" class="add-result-plus" />
            <text v-else class="added-tag">已加入</text>
          </view>
        </scroll-view>
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 歌单管理页：全部使用主题令牌 */
.manage-page { min-height: 100vh; box-sizing: border-box; background: var(--sm-page-bg); }
.page-head { display: flex; align-items: center; gap: 20rpx; padding: calc(env(safe-area-inset-top) + 28rpx) 40rpx 12rpx; }
.back-button { display: grid; place-items: center; width: 64rpx; height: 64rpx; border: 1rpx solid var(--sm-border); border-radius: 50%; background: var(--sm-surface); color: var(--sm-text-2); }
.page-title { flex: 1; color: var(--sm-text); font-size: 30rpx; font-weight: 800; }
.head-action { display: flex; align-items: center; gap: 8rpx; height: 60rpx; padding: 0 26rpx; border-radius: 30rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 800; }

.page-body { height: calc(100vh - 140rpx); padding: 10rpx 40rpx 0; box-sizing: border-box; }
.inline-hint { padding: 60rpx 0; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }

/* 列表态 */
.empty-block { display: flex; flex-direction: column; align-items: center; gap: 14rpx; padding: 120rpx 0; color: var(--sm-text-2); font-size: 20rpx; }
.empty-sub { color: var(--sm-text-3); font-size: 16rpx; text-align: center; }
.collection-row { display: flex; align-items: center; gap: 20rpx; margin-top: 20rpx; padding: 22rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); box-shadow: var(--sm-shadow-sm); }
.collection-cover { display: grid; flex: none; place-items: center; width: 110rpx; height: 110rpx; border-radius: 18rpx; background: var(--sm-cover-bg); color: var(--sm-cover-ink); overflow: hidden; }
.cover-img { width: 100%; height: 100%; }
.collection-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.collection-title { overflow: hidden; color: var(--sm-text); font-size: 22rpx; font-weight: 800; text-overflow: ellipsis; white-space: nowrap; }
.collection-desc { overflow: hidden; color: var(--sm-text-2); font-size: 16rpx; text-overflow: ellipsis; white-space: nowrap; }
.collection-count { color: var(--sm-text-3); font-size: 14rpx; }
.row-actions { display: flex; flex: none; gap: 12rpx; }
.row-action { display: grid; place-items: center; width: 58rpx; height: 58rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface-2); color: var(--sm-text-2); }
.row-action.danger { border-color: var(--sm-danger-soft-border); background: var(--sm-danger-soft); color: var(--sm-danger); }

/* 编辑态表单 */
.form-block { margin-top: 24rpx; padding: 28rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); box-shadow: var(--sm-shadow-sm); }
.field-label { display: block; margin-bottom: 14rpx; color: var(--sm-text-2); font-size: 19rpx; font-weight: 700; }
.field-input { height: 92rpx; padding: 0 24rpx; margin-bottom: 22rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 21rpx; }
.field-input::placeholder { color: var(--sm-text-3); }
.field-textarea { min-height: 140rpx; padding: 20rpx 24rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 21rpx; }
.field-textarea::placeholder { color: var(--sm-text-3); }
.block-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 18rpx; }
.block-head .field-label { margin-bottom: 0; }
.add-track-button { display: flex; align-items: center; gap: 8rpx; height: 58rpx; padding: 0 24rpx; border-radius: 29rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 17rpx; font-weight: 800; }

/* 曲目行 */
.track-row { display: flex; align-items: center; gap: 16rpx; padding: 16rpx 0; border-bottom: 1rpx solid var(--sm-divider); }
.track-index { flex: none; width: 44rpx; color: var(--sm-text-3); font-size: 17rpx; font-weight: 700; text-align: center; }
.track-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 4rpx; }
.track-title { overflow: hidden; color: var(--sm-text); font-size: 20rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.track-artists { overflow: hidden; color: var(--sm-text-2); font-size: 15rpx; text-overflow: ellipsis; white-space: nowrap; }
.track-actions { display: flex; flex: none; gap: 10rpx; }
.track-action { display: grid; place-items: center; width: 50rpx; height: 50rpx; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface-2); color: var(--sm-text-2); }
.track-action.danger { border-color: var(--sm-danger-soft-border); background: var(--sm-danger-soft); color: var(--sm-danger); }

.save-bar { display: flex; align-items: center; justify-content: space-between; gap: 20rpx; margin-top: 30rpx; }
.field-message { flex: 1; color: var(--sm-danger); font-size: 16rpx; }
.save-button { display: flex; flex: none; align-items: center; justify-content: center; height: 88rpx; padding: 0 60rpx; border-radius: 44rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 21rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.save-button.disabled { opacity: .6; }

/* 添加曲目面板 */
.add-panel { position: fixed; inset: 0; z-index: 200; }
.add-panel-mask { position: absolute; inset: 0; background: rgba(8, 10, 14, .52); }
.add-panel-body { position: absolute; bottom: 0; left: 0; right: 0; display: flex; max-height: 78vh; flex-direction: column; padding: 28rpx 40rpx calc(env(safe-area-inset-bottom) + 28rpx); border-radius: 28rpx 28rpx 0 0; background: var(--sm-page-bg); box-shadow: 0 -12rpx 40rpx rgba(0, 0, 0, .35); }
.add-panel-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 20rpx; }
.add-panel-title { color: var(--sm-text); font-size: 24rpx; font-weight: 800; }
.add-panel-close { display: grid; place-items: center; width: 56rpx; height: 56rpx; border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text-2); }
.add-search { display: flex; align-items: center; gap: 14rpx; margin-bottom: 16rpx; }
.add-search-input { flex: 1; height: 84rpx; padding: 0 24rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 20rpx; }
.add-search-input::placeholder { color: var(--sm-text-3); }
.add-search-action { display: grid; place-items: center; width: 84rpx; height: 84rpx; border-radius: 20rpx; background: var(--sm-accent); color: var(--sm-accent-ink); }
.add-results { flex: 1; min-height: 0; }
.add-result-row { display: flex; align-items: center; gap: 16rpx; padding: 18rpx 6rpx; border-bottom: 1rpx solid var(--sm-divider); }
.add-result-row.added { opacity: .55; }
.add-result-plus { flex: none; color: var(--sm-accent); }
.added-tag { flex: none; color: var(--sm-text-3); font-size: 15rpx; }

.bottom-space { height: 60rpx; }
</style>
