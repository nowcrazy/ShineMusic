<script setup lang="ts">
/**
 * 歌曲需求池页：浏览/投票歌曲需求，提交新需求；含“投票优先 / 最新提交 / 我的”三个 tab。
 * 进入参数：无。投票、提交、查看“我的”均需登录（requireLogin 拦截，未登录引导去登录页）。
 * 投票规则：一人一条需求只能点一次赞，且**不可取消**；重复点击不再产生请求，只提示已点过。
 * 对应后端接口：services/requests 的 getSongRequests / getMySongRequests /
 * voteSongRequest / createSongRequest（分页 pageSize=20）。
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronUp, Clock3, Inbox, Plus, X } from 'lucide-vue-next'
import {
  createSongRequest,
  getMySongRequests,
  getSongRequestTimeline,
  getSongRequests,
  SONG_REQUEST_EVENT_LABEL,
  SONG_REQUEST_STATUS,
  voteSongRequest,
  withdrawSongRequest,
  type SongRequestTimelineItem,
} from '@/services/requests'
import { isAdmin, isAuthenticated, requireLogin } from '@/services/session'
import { describeError, ErrorCodes } from '@/services/errors'
import { formatDate } from '@/utils/format'
import type { ApiSongRequest } from '@/types/music'
import { ApiError } from '@/services/http'

// tab 标识：votes=投票优先排序，createdAt=最新提交排序，mine=我提交/投过票的
type Tab = 'votes' | 'createdAt' | 'mine'

const { t } = useI18n()

// tab 切换核心状态与固定 tab 配置（labelKey 为展示层 i18n key，tab 值本身仍是切换逻辑用的英文标识）
const tab = ref<Tab>('votes')
const tabs: { key: Tab; labelKey: string }[] = [
  { key: 'votes', labelKey: 'requestsPage.tabVotes' },
  { key: 'createdAt', labelKey: 'requestsPage.tabLatest' },
  { key: 'mine', labelKey: 'requestsPage.tabMine' },
]

// —— 列表与分页状态 ——
const items = ref<readonly ApiSongRequest[]>([])
const loading = ref(true)
const loadingMore = ref(false)
const loadError = ref(false)
const page = ref(1)
const total = ref(0)
const pageSize = 20
const pendingVotes = ref(new Set<string>()) // 正在提交投票的需求 ID（防重复点击）

// —— 底部提交 sheet 与表单状态 ——
const showForm = ref(false)
const formTitle = ref('')
const formArtist = ref('')
const formAlbum = ref('')
const submitting = ref(false)

// 是否还有下一页（用于“加载更多/已经到底了”切换）
const hasMore = computed(() => items.value.length < total.value)

/**
 * 加载需求列表
 * @param reset true=重置到第一页并覆盖；false=加载下一页并追加
 * 关键副作用：“我的”tab 未登录时清空列表直接返回
 */
async function load(reset = true) {
  if (tab.value === 'mine' && !isAuthenticated.value) {
    items.value = []
    total.value = 0
    loading.value = false
    loadError.value = false
    return
  }
  if (reset) {
    // 重置加载：回到第一页
    loading.value = true
    page.value = 1
  }
  loadError.value = false
  try {
    const result =
      tab.value === 'mine'
        ? await getMySongRequests({ page: page.value, pageSize })
        : await getSongRequests({ sort: tab.value, page: page.value, pageSize })
    items.value = reset ? result.items : [...items.value, ...result.items] // 首页覆盖 / 翻页追加
    total.value = result.total
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
    loadingMore.value = false
  }
}

/**
 * 切换 tab 并重新从第一页加载
 * @param next 目标 tab
 */
function switchTab(next: Tab) {
  tab.value = next
  void load()
}

/** 手动“加载更多”：页码 +1 后追加下一页（loadingMore/hasMore 双重防护） */
async function loadMore() {
  if (loadingMore.value || !hasMore.value) return
  loadingMore.value = true
  page.value += 1
  await load(false)
}

// 每次进入页面刷新列表（登录返回、提交完成返回都能拿到最新投票状态）
onShow(() => {
  void load()
})

/**
 * 点赞（投票）：只能点一次，且点了不能取消。
 * @param item 被点赞的需求
 * 关键副作用：已点过 → 直接提示，不发请求；未点过 → 乐观 +1 并加锁 pendingVotes，失败回滚。
 */
async function handleVote(item: ApiSongRequest) {
  if (!requireLogin()) return
  // 已点过：不再发请求，也不允许取消（产品规则：点赞不可撤销）
  if (item.myVote) {
    uni.showToast({ title: t('requestsPage.alreadyVoted'), icon: 'none' })
    return
  }
  // 同一需求的请求还在路上：忽略连点，避免重复 +1
  if (pendingVotes.value.has(item.id)) return
  const nextVotes = new Set(pendingVotes.value)
  nextVotes.add(item.id)
  pendingVotes.value = nextVotes
  // 乐观更新：先在界面上点亮并 +1
  item.myVote = true
  item.voteCount += 1
  try {
    await voteSongRequest(item.id)
  } catch (error) {
    // 请求失败：回滚乐观更新
    item.myVote = false
    item.voteCount -= 1
    if (error instanceof ApiError && error.code === ErrorCodes.ResourceConflict) {
      uni.showToast({ title: t('requestsPage.voteClosed'), icon: 'none' })
    } else {
      uni.showToast({ title: describeError(error, t('requestsPage.operationFailed')), icon: 'none' })
    }
  } finally {
    const cleared = new Set(pendingVotes.value)
    cleared.delete(item.id)
    pendingVotes.value = cleared
  }
}

/** 打开发布需求的底部 sheet（登录拦截：未登录先跳登录） */
function openForm() {
  if (!requireLogin()) return
  showForm.value = true
}

/** 关闭 sheet 并重置表单字段 */
function closeForm() {
  showForm.value = false
  formTitle.value = ''
  formArtist.value = ''
  formAlbum.value = ''
}

/**
 * 提交歌曲需求：校验歌曲名/艺人必填，成功后关闭表单、切到“我的”并刷新
 * 关键副作用：冲突错误区分“已在曲库中”与“已有相同需求”两种提示
 */
async function submitRequest() {
  const title = formTitle.value.trim()
  const artistName = formArtist.value.trim()
  // 表单校验：歌曲名与艺人均为必填
  if (!title || !artistName) {
    uni.showToast({ title: t('requestsPage.titleArtistRequired'), icon: 'none' })
    return
  }
  submitting.value = true
  try {
    await createSongRequest({
      title,
      artistName,
      albumTitle: formAlbum.value.trim() || undefined,
    })
    uni.showToast({ title: t('requestsPage.submitted'), icon: 'none' })
    closeForm()
    tab.value = 'mine'
    void load()
  } catch (error) {
    const message =
      error instanceof ApiError
        ? error.code === ErrorCodes.ResourceConflict
          ? error.message.includes('library')
            ? t('requestsPage.inLibrary')
            : t('requestsPage.duplicateRequest')
          : error.message
        : t('requestsPage.submitFailed')
    uni.showToast({ title: message, icon: 'none' })
  } finally {
    submitting.value = false
  }
}

/** 安全读取状态元信息：遇到未知状态时回退展示原始值，避免模板报错 */
function statusMeta(status: ApiSongRequest['status']) {
  return SONG_REQUEST_STATUS[status] ?? { label: status, tone: 'open' }
}

/** 是否可撤回：仅“我的”tab 中、本人提交、且仍为 open 的需求 */
function canWithdraw(item: ApiSongRequest) {
  return tab.value === 'mine' && item.createdByMe && item.status === 'open'
}

/** 是否可重新提交：本人提交、已被拒绝或已撤回的需求 */
function canResubmit(item: ApiSongRequest) {
  return (
    tab.value === 'mine' &&
    item.createdByMe &&
    (item.status === 'declined' || item.status === 'withdrawn')
  )
}

/** 管理员：该需求是否可去上传满足（未结束的需求） */
function canFulfill(item: ApiSongRequest) {
  return (
    isAdmin.value &&
    (item.status === 'open' || item.status === 'planned' || item.status === 'in_progress')
  )
}

/** 管理员：跳转导入页并携带需求信息，上传发布后自动满足 */
function handleFulfill(item: ApiSongRequest) {
  const params = [
    `songRequestId=${encodeURIComponent(item.id)}`,
    `title=${encodeURIComponent(item.title)}`,
    `artistName=${encodeURIComponent(item.artistName)}`,
  ]
  if (item.albumTitle) params.push(`albumTitle=${encodeURIComponent(item.albumTitle)}`)
  uni.navigateTo({ url: `/pages/admin/import?${params.join('&')}` })
}

/** 执行撤回（已二次确认）：成功后刷新列表 */
async function doWithdraw(item: ApiSongRequest) {
  try {
    await withdrawSongRequest(item.id)
    uni.showToast({ title: t('requestsPage.withdrawnToast'), icon: 'none' })
    void load()
  } catch (error) {
    uni.showToast({
      title: error instanceof ApiError ? error.message : t('requestsPage.withdrawFailed'),
      icon: 'none',
    })
  }
}

/** 撤回需求：二次确认后调用接口 */
function handleWithdraw(item: ApiSongRequest) {
  uni.showModal({
    title: t('requestsPage.withdrawTitle'),
    content: t('requestsPage.withdrawConfirm', { title: item.title }),
    confirmText: t('requestsPage.withdrawBtn'),
    success: (res) => {
      if (res.confirm) void doWithdraw(item)
    },
  })
}

/** 重新提交：以原需求内容创建一条新需求（被拒绝/撤回后可用） */
async function handleResubmit(item: ApiSongRequest) {
  try {
    await createSongRequest({
      title: item.title,
      artistName: item.artistName,
      albumTitle: item.albumTitle ?? undefined,
    })
    uni.showToast({ title: t('requestsPage.resubmitted'), icon: 'none' })
    void load()
  } catch (error) {
    const message =
      error instanceof ApiError
        ? error.code === ErrorCodes.ResourceConflict
          ? t('requestsPage.duplicateActive')
          : error.message
        : t('requestsPage.submitFailed')
    uni.showToast({ title: message, icon: 'none' })
  }
}

/** 跳转登录页（“我的”tab 未登录引导） */
function goLogin() {
  uni.navigateTo({ url: '/pages/auth/login' })
}

// —— 处理进度（时间线）：按需求 id 缓存，展开时才请求 ——
const expandedTimelines = ref<Record<string, boolean>>({})
const timelines = ref<Record<string, SongRequestTimelineItem[]>>({})
const timelineLoading = ref<Record<string, boolean>>({})

/** 时间线是否已展开 */
function isTimelineOpen(requestId: string) {
  return expandedTimelines.value[requestId] === true
}

/**
 * 展开/收起某条需求的处理进度。
 * 首次展开才拉取时间线（避免列表页一次打几十个请求），失败时收起并提示。
 */
async function toggleTimeline(item: ApiSongRequest) {
  const requestId = item.id
  if (expandedTimelines.value[requestId]) {
    expandedTimelines.value = { ...expandedTimelines.value, [requestId]: false }
    return
  }
  expandedTimelines.value = { ...expandedTimelines.value, [requestId]: true }
  if (timelines.value[requestId] || timelineLoading.value[requestId]) return
  timelineLoading.value = { ...timelineLoading.value, [requestId]: true }
  try {
    timelines.value = { ...timelines.value, [requestId]: await getSongRequestTimeline(requestId) }
  } catch {
    expandedTimelines.value = { ...expandedTimelines.value, [requestId]: false }
    uni.showToast({ title: t('requestsPage.timelineLoadFailed'), icon: 'none' })
  } finally {
    timelineLoading.value = { ...timelineLoading.value, [requestId]: false }
  }
}

/** 时间线事件的中文标签（未知类型回退原始值，避免新增类型时空白） */
function eventLabel(kind: string) {
  return SONG_REQUEST_EVENT_LABEL[kind] ?? kind
}

/** 空函数：绑定在 sheet 内容区 @click.stop，阻止点击事件冒泡到遮罩导致误关闭 */
function swallow() {
  // 仅用于阻止点击事件冒泡到遮罩
}

/**
 * 拼接需求卡片的艺人副标题（有专辑名时追加分隔展示）
 * @param item 需求项
 */
function artistLine(item: ApiSongRequest) {
  return item.albumTitle ? `${item.artistName} · ${item.albumTitle}` : item.artistName
}

/**
 * 投票数展示：千/万以上缩写（1.2k / 3.4w）。
 * 长数字在窄投票栏里会逐位换行竖排，缩写既美观也彻底避免溢出。
 */
function formatCount(n: number): string {
  if (n >= 10000) return `${(n / 10000).toFixed(n % 10000 === 0 ? 0 : 1)}w`
  if (n >= 1000) return `${(n / 1000).toFixed(n % 1000 === 0 ? 0 : 1)}k`
  return String(n)
}
</script>

<template>
  <view class="requests-page">
    <!-- 页头说明 -->
    <view class="head">
      <text class="head-title">{{ $t('requestsPage.title') }}</text>
      <text class="head-sub">{{ $t('requestsPage.subtitle') }}</text>
    </view>

    <!-- 排序 / 我的 tab 切换 -->
    <view class="tabs">
      <view
        v-for="entry in tabs"
        :key="entry.key"
        class="tab-chip"
        :class="{ active: tab === entry.key }"
        @click="switchTab(entry.key)"
      >
        <text>{{ $t(entry.labelKey) }}</text>
      </view>
    </view>

    <!-- “我的”tab 未登录：登录引导 -->
    <view v-if="tab === 'mine' && !isAuthenticated" class="login-tip">
      <Inbox :size="26" />
      <text class="login-tip-title">{{ $t('requestsPage.loginToView') }}</text>
      <view class="login-tip-button" @click="goLogin"><text>{{ $t('auth.goLogin') }}</text></view>
    </view>

    <view v-else-if="loading" class="state-text">{{ $t('requestsPage.loading') }}</view>
    <view v-else-if="loadError" class="state-text">{{ $t('requestsPage.loadFailed') }}</view>
    <view v-else-if="!items.length" class="state-text">
      {{ tab === 'mine' ? $t('requestsPage.emptyMine') : $t('requestsPage.emptyAll') }}
    </view>

    <!-- 需求列表：左侧投票栏 + 需求信息与状态徽章；底部为分页加载区 -->
    <view v-else class="request-list">
      <view v-for="item in items" :key="item.id" class="request-card">
        <view class="vote-column" :class="{ voted: item.myVote }" @click="handleVote(item)">
          <ChevronUp :size="20" :stroke-width="item.myVote ? 3 : 2" />
          <text class="vote-count">{{ formatCount(item.voteCount) }}</text>
        </view>
        <view class="request-copy">
          <view class="title-row">
            <text class="request-title">{{ item.title }}</text>
            <text class="status-badge" :class="`status-${statusMeta(item.status).tone}`">
              {{ statusMeta(item.status).label }}
            </text>
          </view>
          <view class="request-meta">
            <text class="request-artist">{{ artistLine(item) }} · {{ formatDate(item.createdAt) }}</text>
            <!-- 处理进度：展开查看时间线（提交 → 计划 → 处理中 → 已入库） -->
            <view class="timeline-toggle" @click.stop="toggleTimeline(item)">
              <Clock3 :size="13" />
              <text>{{ isTimelineOpen(item.id) ? $t('requestsPage.collapseTimeline') : $t('requestsPage.timelineToggle') }}</text>
              <ChevronDown v-if="!isTimelineOpen(item.id)" :size="13" />
              <ChevronUp v-else :size="13" />
            </view>
          </view>

          <!-- 时间线：按时间正序展示每次状态变化；备注（拒绝原因等）仅本人/管理员可见 -->
          <view v-if="isTimelineOpen(item.id)" class="timeline">
            <view v-if="timelineLoading[item.id]" class="timeline-hint">{{ $t('requestsPage.timelineLoading') }}</view>
            <view v-else class="timeline-list">
              <view
                v-for="(event, index) in timelines[item.id] ?? []"
                :key="event.id"
                class="timeline-item"
              >
                <view class="timeline-rail">
                  <view class="timeline-dot" :class="`dot-${event.kind}`"></view>
                  <view
                    v-if="index < (timelines[item.id]?.length ?? 0) - 1"
                    class="timeline-line"
                  ></view>
                </view>
                <view class="timeline-copy">
                  <text class="timeline-label">{{ eventLabel(event.kind) }}</text>
                  <text class="timeline-time">{{ formatDate(event.createdAt) }}</text>
                  <text v-if="event.note" class="timeline-note">{{ event.note }}</text>
                </view>
              </view>
            </view>
          </view>
          <!-- “我的”tab 撤回/重新提交；管理员可「上传并满足」 -->
          <view
            v-if="canWithdraw(item) || canResubmit(item) || canFulfill(item)"
            class="request-actions"
          >
            <view v-if="canWithdraw(item)" class="action-btn" @click.stop="handleWithdraw(item)">
              <text>{{ $t('requestsPage.withdrawBtn') }}</text>
            </view>
            <view v-if="canResubmit(item)" class="action-btn primary" @click.stop="handleResubmit(item)">
              <text>{{ $t('requestsPage.resubmitBtn') }}</text>
            </view>
            <view v-if="canFulfill(item)" class="action-btn primary" @click.stop="handleFulfill(item)">
              <text>{{ $t('requestsPage.fulfillBtn') }}</text>
            </view>
          </view>
        </view>
      </view>
      <!-- 分页尾部：加载中 / 到底 / 手动加载更多 -->
      <view v-if="loadingMore" class="state-text">{{ $t('requestsPage.loadingMore') }}</view>
      <view v-else-if="!hasMore" class="list-end">{{ $t('requestsPage.endOfList') }}</view>
      <view v-else class="load-more" @click="loadMore"><text>{{ $t('requestsPage.loadMore') }}</text></view>
    </view>

    <!-- 提交需求底部 sheet：点击遮罩关闭，内容区阻止冒泡 -->
    <view v-if="showForm" class="form-mask" @click="closeForm">
      <view class="form-sheet" @click.stop="swallow">
        <view class="form-head">
          <text class="form-title">{{ $t('requestsPage.formTitle') }}</text>
          <view class="form-close" @click="closeForm"><X :size="18" /></view>
        </view>
        <view class="field">
          <text class="field-label">{{ $t('requestsPage.fieldTitle') }}</text>
          <input v-model="formTitle" class="field-input" maxlength="200" :placeholder="$t('requestsPage.placeholderTitle')" />
        </view>
        <view class="field">
          <text class="field-label">{{ $t('requestsPage.fieldArtist') }}</text>
          <input v-model="formArtist" class="field-input" maxlength="200" :placeholder="$t('requestsPage.placeholderArtist')" />
        </view>
        <view class="field">
          <text class="field-label">{{ $t('requestsPage.fieldAlbum') }}</text>
          <input v-model="formAlbum" class="field-input" maxlength="200" :placeholder="$t('requestsPage.placeholderAlbumHint')" />
        </view>
        <view class="form-submit" :class="{ disabled: submitting }" @click="submitRequest">
          <text>{{ submitting ? $t('requestsPage.submitting') : $t('requestsPage.submitBtn') }}</text>
        </view>
      </view>
    </view>

    <!-- FAB：打开发布需求 sheet（sheet 打开时隐藏） -->
    <view v-if="!showForm" class="fab" @click="openForm"><Plus :size="24" /></view>
  </view>
</template>

<style lang="scss" scoped>
/* 歌曲需求池（方向 B · 玻璃质感） */
.requests-page { min-height: 100vh; padding: 30rpx 40rpx 120rpx; box-sizing: border-box; background: var(--sm-page-bg); }
.head { padding: 6rpx 0 0; }
.head-title { display: block; color: var(--sm-text); font-size: 40rpx; font-weight: 800; }
.head-sub { display: block; margin-top: 12rpx; color: var(--sm-text-2); font-size: 18rpx; line-height: 1.6; }
.tabs { display: flex; gap: 14rpx; margin-top: 28rpx; }
.tab-chip { padding: 14rpx 28rpx; border: 1rpx solid var(--sm-border); border-radius: 36rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 19rpx; -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.tab-chip.active { border-color: transparent; background: var(--sm-accent); color: var(--sm-accent-ink); font-weight: 700; }
.state-text { padding: 90rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.request-list { margin-top: 26rpx; display: flex; flex-direction: column; gap: 16rpx; }
.request-card { display: flex; align-items: center; gap: 20rpx; padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
/* 投票栏：紧凑胶囊，高度随内容自适应并垂直居中（不随卡片拉伸成长条）；
   min-width 自适应数字宽度（配合缩写格式，永远单行不竖排），最大宽度封顶 */
.vote-column { display: flex; flex: none; flex-direction: column; align-items: center; justify-content: center; min-width: 92rpx; max-width: 150rpx; padding: 14rpx 12rpx; border: 1rpx solid transparent; border-radius: 20rpx; background: var(--sm-divider); box-sizing: border-box; color: var(--sm-text-2); transition: transform .15s ease; }
.vote-column:active { transform: scale(.94); }
.vote-column.voted { background: var(--sm-accent-soft); border-color: var(--sm-accent-soft-border); color: var(--sm-accent); }
.vote-count { margin-top: 2rpx; font-size: 26rpx; font-weight: 800; line-height: 1.15; white-space: nowrap; }
.request-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 10rpx; }
.title-row { display: flex; flex-wrap: wrap; align-items: center; gap: 6rpx 12rpx; }
.request-title { flex: 1; min-width: 0; color: var(--sm-text); font-size: 26rpx; font-weight: 700; line-height: 1.4; }
.request-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 8rpx 12rpx; }
.status-badge { flex: none; padding: 4rpx 14rpx; border-radius: 20rpx; font-size: 14rpx; font-weight: 700; }
.status-open { background: var(--sm-accent-soft); color: var(--sm-accent); }
.status-planned { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.status-in_progress, .status-progress { background: var(--sm-warn-soft); color: var(--sm-warn); }
.status-fulfilled { background: var(--sm-accent-soft); color: var(--sm-accent-2); }
.status-declined { background: var(--sm-danger-soft); color: var(--sm-danger); }
.status-withdrawn { background: var(--sm-divider); color: var(--sm-text-3); }
/* 处理进度入口：小号文字按钮，点击展开时间线 */
.timeline-toggle { display: inline-flex; align-items: center; gap: 5rpx; margin-left: auto; padding: 4rpx 12rpx; border-radius: 20rpx; background: var(--sm-surface-3); color: var(--sm-text-2); font-size: 13rpx; font-weight: 700; }
/* 时间线：左侧竖线 + 圆点，右侧事件文案 */
.timeline { margin-top: 14rpx; padding: 16rpx 18rpx; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface-2); }
.timeline-hint { color: var(--sm-text-3); font-size: 15rpx; text-align: center; }
.timeline-list { display: flex; flex-direction: column; }
.timeline-item { display: flex; gap: 14rpx; }
.timeline-rail { display: flex; flex: none; flex-direction: column; align-items: center; width: 16rpx; padding-top: 6rpx; }
.timeline-dot { width: 14rpx; height: 14rpx; border-radius: 50%; background: var(--sm-text-3); }
.timeline-dot.dot-submitted, .timeline-dot.dot-reopened { background: var(--sm-accent); }
.timeline-dot.dot-planned { background: var(--sm-accent-2); }
.timeline-dot.dot-in_progress { background: var(--sm-warn); }
.timeline-dot.dot-fulfilled { background: var(--sm-accent); }
.timeline-dot.dot-declined { background: var(--sm-danger); }
.timeline-dot.dot-withdrawn { background: var(--sm-text-3); }
.timeline-line { flex: 1; width: 2rpx; margin: 4rpx 0; background: var(--sm-divider); }
.timeline-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 3rpx; padding-bottom: 16rpx; }
.timeline-label { color: var(--sm-text); font-size: 16rpx; font-weight: 700; }
.timeline-time { color: var(--sm-text-3); font-size: 13rpx; }
.timeline-note { margin-top: 2rpx; padding: 8rpx 12rpx; border-radius: 10rpx; background: var(--sm-surface-3); color: var(--sm-text-2); font-size: 14rpx; line-height: 1.5; }
.request-actions { display: flex; gap: 12rpx; margin-top: 12rpx; }
.action-btn { padding: 8rpx 22rpx; border: 1rpx solid var(--sm-border); border-radius: 24rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 15rpx; font-weight: 700; }
.action-btn.primary { border-color: transparent; background: var(--sm-accent-soft); color: var(--sm-accent); }
.list-end, .load-more { padding: 30rpx 0 10rpx; color: var(--sm-text-3); font-size: 17rpx; text-align: center; }
.load-more { color: var(--sm-accent); font-weight: 700; }
/* 平台适配：悬浮按钮 bottom 用 env(safe-area-inset-bottom) 避开 iPhone 底部安全区 */
.fab { position: fixed; right: 44rpx; bottom: calc(env(safe-area-inset-bottom) + 50rpx); display: grid; place-items: center; width: 96rpx; height: 96rpx; border-radius: 50%; background: var(--sm-accent); color: var(--sm-accent-ink); box-shadow: 0 14rpx 32rpx var(--sm-accent-soft-border); }
.form-mask { position: fixed; z-index: 20; inset: 0; display: flex; align-items: flex-end; background: var(--sm-mask); -webkit-backdrop-filter: blur(4px); backdrop-filter: blur(4px); }
.form-sheet { width: 100%; padding: 34rpx 40rpx calc(env(safe-area-inset-bottom) + 40rpx); border: 1rpx solid var(--sm-border); border-bottom: 0; border-radius: 32rpx 32rpx 0 0; background: var(--sm-bg-soft); }
.form-head { display: flex; align-items: center; justify-content: space-between; }
.form-title { color: var(--sm-text); font-size: 30rpx; font-weight: 800; }
.form-close { display: grid; place-items: center; width: 56rpx; height: 56rpx; border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text-2); }
.field { margin-top: 24rpx; }
.field-label { display: block; margin-bottom: 12rpx; color: var(--sm-text-2); font-size: 18rpx; font-weight: 700; }
.field-input { height: 88rpx; padding: 0 24rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface); color: var(--sm-text); font-size: 22rpx; }
.field-input::placeholder { color: var(--sm-text-3); }
.form-submit { display: flex; align-items: center; justify-content: center; height: 92rpx; margin-top: 34rpx; border-radius: 46rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 21rpx; font-weight: 800; }
.form-submit.disabled { opacity: .6; }
.login-tip { display: flex; flex-direction: column; align-items: center; gap: 18rpx; padding: 110rpx 40rpx; color: var(--sm-text-3); }
.login-tip-title { color: var(--sm-text-2); font-size: 20rpx; }
.login-tip-button { padding: 16rpx 56rpx; border-radius: 42rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 20rpx; font-weight: 800; }
/* #ifdef H5 */
@media (min-width: 700px) {
  .form-sheet { max-width: 720px; margin: 0 auto; }
}
/* #endif */
</style>
