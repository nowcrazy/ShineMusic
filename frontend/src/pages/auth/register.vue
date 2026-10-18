<script setup lang="ts">
/**
 * 邀请注册页（两步式）：先校验邀请令牌，再设置显示名称与密码并自动登录。
 * 进入参数（onLoad query）：token —— 邀请邮件链接中携带的令牌，存在时自动预填。
 * 对应后端接口：services/session 的 validateInvitation（校验）、registerWithInvitation（创建账号）、
 * login（注册成功后自动登录）；并调用 loadFavorites 同步收藏。
 */
import { computed, ref } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { AudioLines, BadgeCheck } from 'lucide-vue-next'
import { login, registerWithInvitation, validateInvitation } from '@/services/session'
import { loadFavorites } from '@/services/library'
import { loadFeedback } from '@/services/feedback'
import { describeError } from '@/services/errors'
import { formatDate } from '@/utils/format'
import type { ApiInvitationValidation } from '@/types/music'

// —— 第一步（令牌校验）状态：当前步骤 / 令牌输入 / 校验中 / 校验通过的邀请信息 ——
const step = ref<'token' | 'form'>('token')
const invitationToken = ref('')
const validating = ref(false)
const invitation = ref<ApiInvitationValidation | null>(null)

// —— 第二步（账号设置）表单状态 ——
const displayName = ref('')
const password = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const errorMessage = ref('')
// 焦点链：显示名 → 密码 → 确认密码（uni 的 focus 属性只在变为 true 时生效，用后需复位）
const focusedField = ref<'' | 'password' | 'confirm'>('')

/** 把焦点移到下一个输入框（供回车/确认键使用） */
function focusNext(field: 'password' | 'confirm') {
  focusedField.value = field
  setTimeout(() => {
    focusedField.value = ''
  }, 120)
}

/** 解析 onLoad query：邀请链接携带 token 时自动填入输入框 */
onLoad((options) => {
  if (options?.token) invitationToken.value = options.token
})

// “验证邀请”按钮可用条件：令牌非空且不在请求中
const canValidate = computed(() => invitationToken.value.trim().length > 0 && !validating.value)

/**
 * 第一步提交：校验邀请令牌，成功后进入账号设置步骤
 * 关键副作用：失败展示中文错误（无效/过期/已使用/网络异常）
 */
async function handleValidate() {
  // 防止回车与「验证邀请」按钮同时触发两次校验
  if (validating.value) return
  errorMessage.value = ''
  validating.value = true
  try {
    invitation.value = await validateInvitation(invitationToken.value.trim())
    step.value = 'form'
  } catch (error) {
    errorMessage.value = invitationError(error)
  } finally {
    validating.value = false
  }
}

/**
 * 密码强度校验：至少 6 位（与后端开发环境策略一致）
 * @returns 校验失败返回错误文案，通过返回 null
 */
function validatePassword(): string | null {
  const value = password.value
  if (value.length < 6) return '密码至少需要 6 位'
  return null
}

/**
 * 第二步提交：依次校验显示名、密码强度、两次密码一致，创建账号后用相同凭据自动登录
 * 关键副作用：成功后 toast 并 reLaunch 到首页（注册页无可靠上一页，直接重开首页）；失败展示接口错误
 */
async function handleRegister() {
  // 防止回车连按/与按钮点击并发触发两次注册
  if (submitting.value) return
  errorMessage.value = ''
  // 表单校验：显示名必填
  if (!displayName.value.trim()) {
    errorMessage.value = '请填写显示名称'
    return
  }
  // 表单校验：密码强度规则
  const passwordError = validatePassword()
  if (passwordError) {
    errorMessage.value = passwordError
    return
  }
  // 表单校验：两次密码必须一致
  if (password.value !== confirmPassword.value) {
    errorMessage.value = '两次输入的密码不一致'
    return
  }
  submitting.value = true
  try {
    const created = await registerWithInvitation({
      invitationToken: invitationToken.value.trim(),
      password: password.value,
      displayName: displayName.value.trim(),
    })
    await login(created.email, password.value)
    loadFavorites(true).catch(() => undefined)
    loadFeedback().catch(() => undefined)
    uni.showToast({ title: '账号已创建', icon: 'none' })
    setTimeout(() => {
      uni.reLaunch({ url: '/pages/home/index' })
    }, 300)
  } catch (error) {
    errorMessage.value = describeError(error, '注册失败，请稍后再试')
  } finally {
    submitting.value = false
  }
}

/**
 * 邀请令牌校验错误转中文提示。文案映射集中在 services/errors.describeError。
 * @param error validateInvitation 抛出的异常（期望为 ApiError）
 * @returns 可直接展示的错误文案
 */
function invitationError(error: unknown): string {
  return describeError(error, '验证失败，请稍后再试')
}
</script>

<template>
  <view class="auth-page">
    <!-- 品牌头与邀请制说明 -->
    <view class="brand-mark"><AudioLines :size="24" :stroke-width="2.7" /></view>
    <text class="auth-title">邀请注册</text>
    <text class="auth-sub">Shine Music 采用管理员邀请制，不开放公开注册</text>

    <!-- 第一步：验证邀请令牌 -->
    <template v-if="step === 'token'">
      <view class="field">
        <text class="field-label">邀请令牌</text>
        <!-- 回车即校验：H5 用 keydown.enter 兜底（textarea 默认会插入换行），移动端用键盘「完成」 -->
        <textarea
          v-model="invitationToken"
          class="field-input token-input"
          :maxlength="200"
          auto-height
          confirm-type="done"
          placeholder="粘贴邀请邮件链接中的令牌"
          @confirm="handleValidate"
          @keydown.enter.prevent="handleValidate"
        />
      </view>
      <text v-if="errorMessage" class="error-text">{{ errorMessage }}</text>
      <view class="primary-button" :class="{ disabled: !canValidate }" @tap="handleValidate">
        <text>{{ validating ? '验证中…' : '验证邀请' }}</text>
      </view>
    </template>

    <!-- 第二步：设置账号 -->
    <template v-else>
      <!-- 校验通过的邀请信息：受邀邮箱与有效期 -->
      <view class="invite-badge">
        <BadgeCheck :size="17" />
        <view class="invite-copy">
          <text class="invite-email">受邀邮箱：{{ invitation?.email }}</text>
          <text class="invite-expiry">有效期至 {{ formatDate(invitation?.expiresAt ?? '') }}</text>
        </view>
      </view>

      <view class="field">
        <text class="field-label">显示名称</text>
        <input
          v-model="displayName"
          class="field-input"
          maxlength="100"
          placeholder="在曲库中显示的名字"
          confirm-type="next"
          @confirm="focusNext('password')"
        />
      </view>
      <view class="field">
        <text class="field-label">密码</text>
        <input
          v-model="password"
          class="field-input"
          password
          placeholder="至少 6 位"
          confirm-type="next"
          :focus="focusedField === 'password'"
          @confirm="focusNext('confirm')"
        />
      </view>
      <view class="field">
        <text class="field-label">确认密码</text>
        <!-- 最后一个输入框回车直接创建账号，不需要点按钮 -->
        <input
          v-model="confirmPassword"
          class="field-input"
          password
          placeholder="再输入一次密码"
          confirm-type="done"
          :focus="focusedField === 'confirm'"
          @confirm="handleRegister"
        />
      </view>

      <text v-if="errorMessage" class="error-text">{{ errorMessage }}</text>
      <view class="primary-button" :class="{ disabled: submitting }" @tap="handleRegister">
        <text>{{ submitting ? '创建中…' : '创建账号并登录' }}</text>
      </view>
    </template>
  </view>
</template>

<style lang="scss" scoped>
/* 邀请注册页（方向 B · 玻璃质感） */
/* 平台适配：顶部 padding 用 env(safe-area-inset-top) 避开刘海/状态栏 */
.auth-page { display: flex; min-height: 100vh; flex-direction: column; padding: calc(env(safe-area-inset-top) + 80rpx) 56rpx 60rpx; box-sizing: border-box; background: var(--sm-page-bg); }
.brand-mark { display: grid; place-items: center; width: 80rpx; height: 80rpx; border-radius: 24rpx; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); color: var(--sm-accent-3); -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.auth-title { margin-top: 36rpx; color: var(--sm-text); font-size: 44rpx; font-weight: 800; }
.auth-sub { margin-top: 14rpx; color: var(--sm-text-2); font-size: 20rpx; line-height: 1.6; }
.field { margin-top: 30rpx; }
.field-label { display: block; margin-bottom: 14rpx; color: var(--sm-text-2); font-size: 19rpx; font-weight: 700; }
.field-input { height: 96rpx; padding: 0 26rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); color: var(--sm-text); font-size: 23rpx; box-sizing: border-box; -webkit-backdrop-filter: blur(14px); backdrop-filter: blur(14px); }
.field-input::placeholder { color: var(--sm-text-3); }
.token-input { height: auto; min-height: 150rpx; padding: 22rpx 26rpx; line-height: 1.6; }
.error-text { margin-top: 24rpx; color: var(--sm-danger); font-size: 18rpx; }
.primary-button { display: flex; align-items: center; justify-content: center; height: 98rpx; margin-top: 40rpx; border-radius: 49rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 22rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.primary-button.disabled { opacity: .5; }
.invite-badge { display: flex; align-items: center; gap: 14rpx; margin-top: 34rpx; padding: 22rpx 24rpx; border: 1rpx solid var(--sm-accent-glow); border-radius: 18rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.invite-copy { display: flex; flex: 1; flex-direction: column; gap: 5rpx; }
.invite-email { color: var(--sm-text); font-size: 19rpx; font-weight: 700; }
.invite-expiry { color: var(--sm-text-2); font-size: 16rpx; }
</style>
