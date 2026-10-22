<script setup lang="ts">
/**
 * 管理员邀请管理页：创建邀请、查看邀请列表、重发/撤销邀请。
 * 访问控制：onShow 校验 isAdmin，非管理员 redirectTo 首页。
 * 对应后端接口：services/admin-invitation
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import {
  Mail,
  Plus,
  KeyRound,
  RefreshCw,
  ShieldCheck,
  Trash2,
  X,
} from 'lucide-vue-next'
import { isAdmin } from '@/services/session'
import {
  createInvitation,
  createInvitationCode,
  getInvitations,
  resendInvitation,
  revokeInvitation,
  type Invitation,
} from '@/services/admin-invitation'

const { t } = useI18n()
const email = ref('')
const invitations = ref<readonly Invitation[]>([])
const loading = ref(false)
const creating = ref(false)
const generating = ref(false)
const loadError = ref('')

// 权限拦截
onShow(() => {
  if (!isAdmin.value) {
    uni.redirectTo({ url: '/pages/home/index' })
    return
  }
  void loadInvitations()
})

/** 拉取邀请列表 */
async function loadInvitations() {
  loading.value = true
  loadError.value = ''
  try {
    const result = await getInvitations()
    invitations.value = result.items
  } catch {
    loadError.value = t('invitationAdmin.loadError')
  } finally {
    loading.value = false
  }
}

/** 创建邀请 */
async function handleCreate() {
  if (!email.value.trim() || creating.value) return
  const trimmed = email.value.trim()
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmed)) {
    uni.showToast({ title: t('invitationAdmin.invalidEmail'), icon: 'none' })
    return
  }
  creating.value = true
  try {
    await createInvitation(trimmed)
    uni.showToast({ title: t('invitationAdmin.sent'), icon: 'success' })
    email.value = ''
    await loadInvitations()
  } catch (err: any) {
    const msg = err?.message || t('invitationAdmin.sendFailed')
    uni.showToast({ title: msg, icon: 'none' })
  } finally {
    creating.value = false
  }
}

/** 生成注册码/CDK：明文仅展示一次，随后刷新列表 */
async function handleCreateCode() {
  if (generating.value) return
  generating.value = true
  try {
    const result = await createInvitationCode()
    uni.showModal({
      title: t('invitationAdmin.codeTitle'),
      content: t('invitationAdmin.codeModalContent', { code: result.code }),
      showCancel: false,
      confirmText: t('invitationAdmin.done'),
    })
    await loadInvitations()
  } catch (err: any) {
    const msg = err?.message || t('invitationAdmin.codeCreateFailed')
    uni.showToast({ title: msg, icon: 'none' })
  } finally {
    generating.value = false
  }
}

/** 重发邀请 */
async function handleResend(id: string) {
  try {
    await resendInvitation(id)
    uni.showToast({ title: t('invitationAdmin.resent'), icon: 'success' })
    await loadInvitations()
  } catch {
    uni.showToast({ title: t('invitationAdmin.resendFailed'), icon: 'none' })
  }
}

/** 撤销邀请 */
async function handleRevoke(id: string) {
  uni.showModal({
    title: t('invitationAdmin.revokeTitle'),
    content: t('invitationAdmin.revokeConfirm'),
    confirmText: t('invitationAdmin.revoke'),
    confirmColor: '#d0553a',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await revokeInvitation(id)
        uni.showToast({ title: t('invitationAdmin.revoked'), icon: 'success' })
        await loadInvitations()
      } catch {
        uni.showToast({ title: t('invitationAdmin.revokeFailed'), icon: 'none' })
      }
    },
  })
}

/** 状态标签 */
function statusLabel(status: string): string {
  const map: Record<string, string> = {
    created: t('invitationAdmin.statusCreated'),
    sent: t('invitationAdmin.statusSent'),
    issued: t('invitationAdmin.statusIssuedGenerated'),
    delivery_failed: t('invitationAdmin.statusDeliveryFailed'),
    redeemed: t('invitationAdmin.statusRedeemed'),
    expired: t('invitationAdmin.statusExpired'),
    revoked: t('invitationAdmin.statusRevoked'),
  }
  return map[status] || status
}

/** 状态样式 */
function statusClass(status: string): string {
  switch (status) {
    case 'sent': return 'status-sent'
    case 'issued': return 'status-pending'
    case 'redeemed': return 'status-redeemed'
    case 'revoked':
    case 'expired': return 'status-inactive'
    case 'delivery_failed': return 'status-error'
    default: return 'status-pending'
  }
}

/** 格式化时间 */
function formatDate(dateStr: string): string {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleString()
}
</script>

<template>
  <scroll-view class="invite-page" scroll-y :show-scrollbar="false">
    <!-- 页头 -->
    <view class="page-heading">
      <view class="heading-icon"><ShieldCheck :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('invitationAdmin.pageTitle') }}</text>
        <text class="heading-subtitle">{{ $t('invitationAdmin.subtitle') }}</text>
      </view>
    </view>

    <!-- 创建邀请表单 -->
    <view class="create-card">
      <view class="form-row">
        <view class="email-input">
          <Mail :size="16" />
          <input v-model="email" :placeholder="$t('invitationAdmin.emailPlaceholder')" confirm-type="done" @confirm="handleCreate" />
        </view>
        <view class="create-btn" :class="{ disabled: creating }" @tap="handleCreate">
          <Plus :size="16" />
          <text>{{ creating ? $t('invitationAdmin.sending') : $t('invitationAdmin.send') }}</text>
        </view>
      </view>
      <view class="form-row code-row">
        <view class="code-btn" :class="{ disabled: generating }" @tap="handleCreateCode">
          <KeyRound :size="16" />
          <text>{{ generating ? $t('invitationAdmin.generating') : $t('invitationAdmin.generateCode') }}</text>
        </view>
      </view>
    </view>

    <!-- 邀请列表 -->
    <view class="list-header">
      <text class="list-title">{{ $t('invitationAdmin.listTitle') }}</text>
      <view class="refresh-btn" @tap="loadInvitations">
        <RefreshCw :size="14" />
      </view>
    </view>

    <view v-if="loading" class="state-message">{{ $t('adminCommon.loading') }}</view>
    <view v-else-if="loadError" class="state-message error">{{ loadError }}</view>
    <view v-else-if="!invitations.length" class="state-message">
      <Mail :size="28" />
      <text>{{ $t('invitationAdmin.empty') }}</text>
    </view>

    <view v-else class="invite-list">
      <view v-for="inv in invitations" :key="inv.id" class="invite-card">
        <view class="invite-main">
          <text class="invite-email">{{ inv.email || $t('invitationAdmin.codeNoEmail') }}</text>
          <text class="invite-status" :class="statusClass(inv.status)">{{ statusLabel(inv.status) }}</text>
        </view>
        <view class="invite-meta">
          <text>{{ $t('invitationAdmin.createdAt', { date: formatDate(inv.createdAt) }) }}</text>
          <text>{{ $t('invitationAdmin.expiresAt', { date: formatDate(inv.expiresAt) }) }}</text>
        </view>
        <view v-if="inv.redeemedAt" class="invite-meta">
          <text>{{ $t('invitationAdmin.redeemedAt', { date: formatDate(inv.redeemedAt) }) }}</text>
        </view>
        <view class="invite-actions">
          <view
            v-if="inv.status === 'created' || inv.status === 'sent' || inv.status === 'delivery_failed'"
            class="action-btn resend"
            @tap="handleResend(inv.id)"
          >
            <RefreshCw :size="13" /><text>{{ $t('invitationAdmin.resend') }}</text>
          </view>
          <view
            v-if="inv.status === 'created' || inv.status === 'sent' || inv.status === 'issued' || inv.status === 'delivery_failed'"
            class="action-btn revoke"
            @tap="handleRevoke(inv.id)"
          >
            <Trash2 :size="13" /><text>{{ $t('invitationAdmin.revoke') }}</text>
          </view>
        </view>
      </view>
    </view>

    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 邀请管理（方向 B · 玻璃质感）：深色底 + 彩色光晕 + 毛玻璃卡片 */
.invite-page { height: 100vh; padding: 30rpx 34rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 28rpx; }
.heading-icon { display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 18rpx; background: var(--sm-accent-3-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent-3); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-2); font-size: 17rpx; }

.create-card { padding: 20rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.form-row { display: flex; gap: 12rpx; }
.email-input { display: flex; flex: 1; align-items: center; gap: 10rpx; height: 76rpx; padding: 0 16rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 12rpx; background: var(--sm-surface-2); color: var(--sm-text-2); }
.email-input input { flex: 1; min-width: 0; color: var(--sm-text); font-size: 17rpx; }
.email-input input::placeholder { color: var(--sm-text-3); }
.create-btn { display: flex; align-items: center; gap: 8rpx; padding: 0 24rpx; height: 76rpx; border-radius: 12rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 17rpx; font-weight: 700; }
.create-btn.disabled { opacity: .55; }

.code-row { margin-top: 20rpx; }
.code-btn { display: flex; flex: 1; align-items: center; justify-content: center; gap: 8rpx; height: 76rpx; border-radius: 12rpx; border: 1rpx solid var(--sm-border); background: var(--sm-surface); color: var(--sm-text-1); font-size: 17rpx; font-weight: 700; }
.code-btn.disabled { opacity: .55; }

.list-header { display: flex; align-items: center; justify-content: space-between; margin-top: 28rpx; margin-bottom: 14rpx; }
.list-title { color: var(--sm-text); font-size: 20rpx; font-weight: 800; }
.refresh-btn { display: grid; place-items: center; width: 44rpx; height: 44rpx; border-radius: 10rpx; background: var(--sm-surface-3); color: var(--sm-text-2); border: 1rpx solid var(--sm-border); }

.state-message { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12rpx; min-height: 200rpx; color: var(--sm-text-2); font-size: 18rpx; }
.state-message.error { color: var(--sm-danger); }

.invite-list { display: flex; flex-direction: column; gap: 14rpx; }
.invite-card { padding: 18rpx 20rpx; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.invite-main { display: flex; align-items: center; justify-content: space-between; gap: 10rpx; }
.invite-email { color: var(--sm-text); font-size: 18rpx; font-weight: 700; }
.invite-status { padding: 5rpx 14rpx; border-radius: 16rpx; font-size: 13rpx; font-weight: 600; }
.invite-meta { display: flex; gap: 16rpx; margin-top: 8rpx; color: var(--sm-text-3); font-size: 14rpx; }
.invite-actions { display: flex; gap: 10rpx; margin-top: 14rpx; }
.action-btn { display: flex; align-items: center; gap: 5rpx; padding: 8rpx 16rpx; border-radius: 8rpx; font-size: 14rpx; font-weight: 600; }
.action-btn.resend { background: var(--sm-accent-soft); color: var(--sm-accent); }
.action-btn.revoke { background: var(--sm-danger-soft); color: var(--sm-danger); }

.status-sent { background: var(--sm-accent-soft); color: var(--sm-accent); }
.status-redeemed { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.status-inactive { background: var(--sm-surface-3); color: var(--sm-text-2); }
.status-error { background: var(--sm-danger-soft); color: var(--sm-danger); }
.status-pending { background: var(--sm-warn-soft); color: var(--sm-warn); }

.bottom-space { height: 50rpx; }
</style>
