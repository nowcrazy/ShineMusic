<script setup lang="ts">
/**
 * 登录页：邮箱 + 密码登录，登录成功后预拉收藏并返回来源页。
 * 进入参数：无（通常由登录拦截 requireLogin 或收藏 tab 跳入）。
 * 对应后端接口：services/session.login；成功后附带 services/library.loadFavorites 同步收藏。
 * 键盘交互：邮箱框回车 → 焦点移到密码框；密码框回车 → 直接登录，无需点按钮。
 */
import { ref } from 'vue'
import { AudioLines } from 'lucide-vue-next'
import { login } from '@/services/session'
import { loadFavorites } from '@/services/library'
import { loadFeedback } from '@/services/feedback'
import { ApiError } from '@/services/http'

// 表单状态：邮箱 / 密码 / 提交中（防重复点击）/ 错误文案
const email = ref('')
const password = ref('')
const submitting = ref(false)
const errorMessage = ref('')
// 密码框聚焦标记：邮箱框回车后把光标送到密码框（uni 的 focus 属性只在变为 true 时生效）
const passwordFocused = ref(false)

/** 邮箱框回车：不直接提交，先把焦点移到密码框，少按一次 Tab */
function focusPassword() {
  passwordFocused.value = true
  setTimeout(() => {
    passwordFocused.value = false
  }, 120)
}

/**
 * 将后端/网络错误转换为中文友好提示
 * @param error login 抛出的异常（期望为 ApiError）
 * @returns 可直接展示的错误文案
 */
function friendlyError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.code === 'authentication_required') return '邮箱或密码不正确'
    if (error.code === 'validation_error') return error.message
    if (error.status === 429) return '尝试次数过多，请稍后再试'
    if (error.status === 0) return '无法连接服务器，请检查网络或 API 配置'
    return error.message
  }
  return '登录失败，请稍后再试'
}

/**
 * 提交登录：先做非空校验，成功后拉取收藏并返回来源页。
 * 导航策略：检查当前页面栈深度，有上一页时 navigateBack 返回；
 * 无上一页（直接通过 URL 进入登录页等场景）时 reLaunch 回首页。
 * 不依赖 navigateBack 的 fail 回调：H5 端 navigateBack 内部调用 router.go(-1)
 * 并总是 resolve 成功，fail 回调永远不会被触发，会导致页面卡死在登录页。
 */
async function handleLogin() {
  // 防止回车连按/与按钮点击并发触发两次登录
  if (submitting.value) return
  // 表单校验：邮箱与密码均必填
  if (!email.value.trim() || !password.value) {
    errorMessage.value = '请输入邮箱和密码'
    return
  }
  submitting.value = true
  errorMessage.value = ''
  try {
    await login(email.value, password.value)
    loadFavorites(true).catch(() => undefined)
    loadFeedback().catch(() => undefined)
    uni.showToast({ title: '欢迎回来', icon: 'none' })
    // getCurrentPages 在 H5/App/小程序均可用，返回当前页面栈数组
    const pages = getCurrentPages()
    setTimeout(() => {
      if (pages.length > 1) {
        uni.navigateBack()
      } else {
        uni.reLaunch({ url: '/pages/home/index' })
      }
    }, 300)
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    submitting.value = false
  }
}

/** 跳转邀请注册页 */
function goRegister() {
  uni.navigateTo({ url: '/pages/auth/register' })
}
</script>

<template>
  <view class="auth-page">
    <!-- 品牌头与标题 -->
    <view class="brand-mark"><AudioLines :size="24" :stroke-width="2.7" /></view>
    <text class="auth-title">登录 Shine Music</text>
    <text class="auth-sub">收藏、歌单与歌曲需求会在设备间同步</text>

    <!-- 登录表单：邮箱 / 密码 / 行内错误 / 提交按钮；两个输入框均支持回车提交 -->
    <view class="field">
      <text class="field-label">邮箱</text>
      <input
        v-model="email"
        class="field-input"
        type="text"
        placeholder="you@example.com"
        confirm-type="next"
        @confirm="focusPassword"
      />
    </view>
    <view class="field">
      <text class="field-label">密码</text>
      <input
        v-model="password"
        class="field-input"
        password
        placeholder="请输入密码"
        confirm-type="done"
        :focus="passwordFocused"
        @confirm="handleLogin"
      />
    </view>

    <text v-if="errorMessage" class="error-text">{{ errorMessage }}</text>

    <view class="primary-button" :class="{ disabled: submitting }" @tap="handleLogin">
      <text>{{ submitting ? '登录中…' : '登录' }}</text>
    </view>
    <!-- 邀请注册入口 -->
    <view class="secondary-link" @tap="goRegister">
      <text>收到邀请邮件？<text class="link">使用邀请令牌注册</text></text>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 登录页（方向 B · 玻璃质感） */
/* 平台适配：顶部 padding 用 env(safe-area-inset-top) 避开刘海/状态栏 */
.auth-page { display: flex; min-height: 100vh; flex-direction: column; padding: calc(env(safe-area-inset-top) + 80rpx) 56rpx 60rpx; box-sizing: border-box; background: var(--sm-page-bg); }
.brand-mark { display: grid; place-items: center; width: 80rpx; height: 80rpx; border-radius: 24rpx; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); color: var(--sm-accent); -webkit-backdrop-filter: blur(10px); backdrop-filter: blur(10px); }
.auth-title { margin-top: 36rpx; color: var(--sm-text); font-size: 44rpx; font-weight: 800; }
.auth-sub { margin-top: 14rpx; color: var(--sm-text-2); font-size: 20rpx; line-height: 1.6; }
.field { margin-top: 34rpx; }
.field-label { display: block; margin-bottom: 14rpx; color: var(--sm-text-2); font-size: 19rpx; font-weight: 700; }
.field-input { height: 96rpx; padding: 0 26rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); color: var(--sm-text); font-size: 23rpx; -webkit-backdrop-filter: blur(14px); backdrop-filter: blur(14px); }
.field-input::placeholder { color: var(--sm-text-3); }
.error-text { margin-top: 24rpx; color: var(--sm-danger); font-size: 18rpx; }
.primary-button { display: flex; align-items: center; justify-content: center; height: 98rpx; margin-top: 44rpx; border-radius: 49rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 22rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.primary-button.disabled { opacity: .6; }
.secondary-link { margin-top: 30rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.link { color: var(--sm-accent); font-weight: 700; }
</style>
