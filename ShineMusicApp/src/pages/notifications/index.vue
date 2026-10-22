<script setup lang="ts">
/**
 * 站内信页：查看系统与需求相关的通知（需求被拒绝/入库/重新开启等）。
 *
 * 交互约定：
 * - 未读消息左侧显示高亮圆点，点击后自动标记已读并按 linkUrl 跳转（如需求池）；
 * - 顶部「全部已读」一键清空未读角标；
 * - 未登录时展示登录引导（消息接口需要鉴权）。
 *
 * 对应后端接口：services/notifications（GET 列表 / POST read / POST read-all）。
 */
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { onShow } from '@dcloudio/uni-app'
import { Bell, CheckCheck, Inbox, Music4, RotateCcw, XCircle } from 'lucide-vue-next'
import {
  getNotifications,
  markAllNotificationsRead,
  markNotificationRead,
  refreshUnreadCount,
  type ApiNotification,
} from '@/services/notifications'
import { isAuthenticated } from '@/services/session'
import { formatDate } from '@/utils/format'
import { ApiError } from '@/services/http'

const { t } = useI18n()

const items = ref<readonly ApiNotification[]>([])
const loading = ref(true)
const loadError = ref(false)
const busy = ref(false)

/** 按消息类型取图标：拒绝=叉、入库=音符、重新开启=回转，其余用铃铛 */
function kindIcon(kind: string) {
  if (kind === 'song_request.declined') return XCircle
  if (kind === 'song_request.fulfilled') return Music4
  if (kind === 'song_request.reopened') return RotateCcw
  return Bell
}

/** 按消息类型取色调类名（影响图标底色） */
function kindTone(kind: string) {
  if (kind === 'song_request.declined') return 'danger'
  if (kind === 'song_request.fulfilled') return 'success'
  if (kind === 'song_request.reopened') return 'info'
  return 'neutral'
}

/** 加载消息列表（登录后才请求） */
async function load() {
  if (!isAuthenticated.value) {
    items.value = []
    loading.value = false
    return
  }
  loading.value = true
  loadError.value = false
  try {
    const result = await getNotifications({ pageSize: 50 })
    items.value = result.items
    await refreshUnreadCount()
  } catch {
    loadError.value = true
  } finally {
    loading.value = false
  }
}

onShow(() => {
  void load()
})

/**
 * 点击消息：先标记已读（本地即时置灰），再按 linkUrl 跳转。
 * 标记失败不回滚列表展示，避免用户看到「点了没反应」。
 */
async function handleOpen(item: ApiNotification) {
  if (!item.isRead) {
    item.isRead = true
    try {
      await markNotificationRead(item.id)
    } catch {
      // 静默：已读状态以服务端为准，下次进入会重新拉取
    }
  }
  if (item.linkUrl) {
    uni.navigateTo({
      url: item.linkUrl,
      fail: () => uni.showToast({ title: t('notifications.pageUnavailable'), icon: 'none' }),
    })
  }
}

/** 全部标记已读 */
async function handleReadAll() {
  if (busy.value) return
  busy.value = true
  try {
    await markAllNotificationsRead()
    items.value = items.value.map((item) => ({ ...item, isRead: true }))
    uni.showToast({ title: t('notifications.allMarkedRead'), icon: 'none' })
  } catch (error) {
    uni.showToast({
      title: error instanceof ApiError ? error.message : t('notifications.operationFailed'),
      icon: 'none',
    })
  } finally {
    busy.value = false
  }
}

/** 跳转登录页 */
function goLogin() {
  uni.navigateTo({ url: '/pages/auth/login' })
}
</script>

<template>
  <scroll-view class="notify-page" scroll-y :show-scrollbar="false">
    <!-- 页头：标题 + 全部已读 -->
    <view class="page-heading">
      <view class="heading-copy">
        <text class="heading-title">{{ $t('notifications.title') }}</text>
        <text class="heading-subtitle">{{ $t('notifications.subtitle') }}</text>
      </view>
      <view
        v-if="items.some((item) => !item.isRead)"
        class="read-all"
        :class="{ disabled: busy }"
        @tap="handleReadAll"
      >
        <CheckCheck :size="15" />
        <text>{{ $t('notifications.markAllRead') }}</text>
      </view>
    </view>

    <!-- 未登录引导 -->
    <view v-if="!isAuthenticated" class="state-empty">
      <Inbox :size="28" />
      <text class="state-title">{{ $t('notifications.loginToView') }}</text>
      <view class="login-button" @tap="goLogin"><text>{{ $t('auth.goLogin') }}</text></view>
    </view>

    <view v-else-if="loading" class="state-text">{{ $t('notifications.loading') }}</view>
    <view v-else-if="loadError" class="state-text">{{ $t('notifications.loadFailed') }}</view>
    <view v-else-if="!items.length" class="state-empty">
      <Bell :size="28" />
      <text class="state-title">{{ $t('notifications.empty') }}</text>
      <text class="state-hint">{{ $t('notifications.emptyHint') }}</text>
    </view>

    <!-- 消息列表 -->
    <view v-else class="notify-list">
      <view
        v-for="item in items"
        :key="item.id"
        class="notify-card"
        :class="{ unread: !item.isRead }"
        @tap="handleOpen(item)"
      >
        <view class="notify-icon" :class="kindTone(item.kind)">
          <component :is="kindIcon(item.kind)" :size="17" />
        </view>
        <view class="notify-copy">
          <view class="notify-title-row">
            <text class="notify-title">{{ item.title }}</text>
            <view v-if="!item.isRead" class="unread-dot"></view>
          </view>
          <text v-if="item.body" class="notify-body">{{ item.body }}</text>
          <text class="notify-time">{{ formatDate(item.createdAt) }}</text>
        </view>
      </view>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 站内信列表（玻璃质感，与需求池/管理页统一） */
.notify-page { min-height: 100vh; padding: 30rpx 40rpx 60rpx; box-sizing: border-box; background: var(--sm-page-bg); }
.page-heading { display: flex; align-items: center; justify-content: space-between; gap: 16rpx; padding: 6rpx 0 24rpx; }
.heading-copy { display: flex; flex-direction: column; }
.heading-title { color: var(--sm-text); font-size: 38rpx; font-weight: 800; }
.heading-subtitle { margin-top: 8rpx; color: var(--sm-text-2); font-size: 17rpx; }
.read-all { display: inline-flex; flex: none; align-items: center; gap: 6rpx; padding: 12rpx 22rpx; border: 1rpx solid var(--sm-border); border-radius: 30rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 15rpx; font-weight: 700; }
.read-all.disabled { opacity: .55; }
.state-text { padding: 90rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.state-empty { display: flex; flex-direction: column; align-items: center; gap: 14rpx; padding: 110rpx 30rpx; color: var(--sm-text-3); }
.state-title { color: var(--sm-text-2); font-size: 20rpx; font-weight: 700; }
.state-hint { color: var(--sm-text-3); font-size: 16rpx; }
.login-button { margin-top: 10rpx; padding: 16rpx 56rpx; border-radius: 42rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 19rpx; font-weight: 800; }
.notify-list { display: flex; flex-direction: column; gap: 14rpx; }
.notify-card { display: flex; gap: 18rpx; padding: 24rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
/* 未读卡片：左侧强调色描边 + 轻微提亮 */
.notify-card.unread { border-color: var(--sm-accent-soft-border); background: var(--sm-surface-2); }
.notify-icon { display: grid; flex: none; place-items: center; width: 62rpx; height: 62rpx; border-radius: 18rpx; }
.notify-icon.neutral { background: var(--sm-surface-3); color: var(--sm-text-2); }
.notify-icon.success { background: var(--sm-accent-soft); color: var(--sm-accent); }
.notify-icon.danger { background: var(--sm-danger-soft); color: var(--sm-danger); }
.notify-icon.info { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.notify-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.notify-title-row { display: flex; align-items: center; gap: 10rpx; }
.notify-title { flex: 1; color: var(--sm-text); font-size: 21rpx; font-weight: 700; }
.unread-dot { flex: none; width: 12rpx; height: 12rpx; border-radius: 50%; background: var(--sm-accent); }
.notify-body { color: var(--sm-text-2); font-size: 17rpx; line-height: 1.55; }
.notify-time { color: var(--sm-text-3); font-size: 14rpx; }
.bottom-space { height: 40rpx; }
</style>
