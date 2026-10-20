<script setup lang="ts">
/**
 * 个人资料页：头像设置 + 昵称修改 + 登录密码修改。
 * 进入方式：首页头像抽屉 →「个人资料」。
 * 对应后端接口：services/profile（PATCH /me、POST/DELETE /me/avatar、POST /me/password）。
 * 邮箱为邀请绑定账号标识，仅展示不可修改。
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { Camera, ChevronLeft, KeyRound, Trash2, UserRound } from 'lucide-vue-next'
import { currentUser, isAuthenticated, loadCurrentUser } from '@/services/session'
import { changePassword, deleteAvatar, updateDisplayName, uploadAvatar } from '@/services/profile'
import { ApiError, resolveUrl } from '@/services/http'
import { describeError } from '@/services/errors'
import { estimatePasswordStrength, passwordStrengthFill } from '@/utils/password-strength'
import { useCapabilities } from '@/services/capabilities'

// —— 昵称修改 ——
const displayName = ref('')
const savingName = ref(false)
const nameMessage = ref('')

// —— 头像 ——
const uploadingAvatar = ref(false)
const avatarMessage = ref('')

// —— 密码修改 ——
const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const savingPassword = ref(false)
const passwordMessage = ref('')

// 密码强度：实时强度条 + 门槛判定，门槛取系统能力下发的 minimumPasswordStrength
const capabilities = useCapabilities()
const minimumPasswordStrength = computed(() => capabilities.capabilities.minimumPasswordStrength)
const passwordStrengthLevel = computed(() => estimatePasswordStrength(newPassword.value))
const passwordStrengthWidth = computed(() => `${passwordStrengthFill(newPassword.value)}%`)
const STRENGTH_LABELS = ['很弱', '较弱', '一般', '较强', '很强']
const STRENGTH_COLORS = ['#e05a3f', '#e08a3f', '#e0c23f', '#8fc93f', '#3fc96a']
const passwordStrengthColor = computed(() => STRENGTH_COLORS[passwordStrengthLevel.value] ?? STRENGTH_COLORS[0])

/** 头像首字母回退（无上传头像时展示） */
const avatarLetter = computed(() => currentUser.value?.displayName?.slice(0, 1).toUpperCase() || 'S')
/** 是否已上传头像 */
const hasAvatar = computed(() => Boolean(currentUser.value?.avatarUrl))

// 每次进入页面同步最新资料（头像/昵称可能在其它入口变更过）
onShow(() => {
  if (isAuthenticated.value && currentUser.value) {
    displayName.value = currentUser.value.displayName
  }
})

/** 保存昵称 */
async function handleSaveName() {
  const name = displayName.value.trim()
  if (!name) {
    nameMessage.value = '昵称不能为空'
    return
  }
  if (name.length > 100) {
    nameMessage.value = '昵称最多 100 个字符'
    return
  }
  savingName.value = true
  nameMessage.value = ''
  try {
    await updateDisplayName(name)
    await loadCurrentUser()
    uni.showToast({ title: '昵称已更新', icon: 'none' })
  } catch (error) {
    nameMessage.value = friendlyError(error)
  } finally {
    savingName.value = false
  }
}

/** 选择并上传头像（相册/拍照，压缩图） */
function handleChangeAvatar() {
  if (uploadingAvatar.value) return
  uni.chooseImage({
    count: 1,
    sizeType: ['compressed'],
    sourceType: ['album', 'camera'],
    success: async ({ tempFilePaths }) => {
      const filePath = tempFilePaths[0]
      if (!filePath) return
      uploadingAvatar.value = true
      avatarMessage.value = ''
      try {
        await uploadAvatar(filePath)
        await loadCurrentUser()
        uni.showToast({ title: '头像已更新', icon: 'none' })
      } catch (error) {
        avatarMessage.value = friendlyError(error)
      } finally {
        uploadingAvatar.value = false
      }
    },
  })
}

/** 删除头像（二次确认） */
function handleDeleteAvatar() {
  uni.showModal({
    title: '删除头像',
    content: '删除后将恢复为字母头像，确定继续？',
    confirmText: '删除',
    success: async ({ confirm }) => {
      if (!confirm) return
      avatarMessage.value = ''
      try {
        await deleteAvatar()
        await loadCurrentUser()
        uni.showToast({ title: '头像已删除', icon: 'none' })
      } catch (error) {
        avatarMessage.value = friendlyError(error)
      }
    },
  })
}

/** 保存新密码 */
async function handleSavePassword() {
  if (!currentPassword.value || !newPassword.value || !confirmPassword.value) {
    passwordMessage.value = '请填写完整的密码信息'
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    passwordMessage.value = '两次输入的新密码不一致'
    return
  }
  if (newPassword.value.length < 8) {
    passwordMessage.value = '新密码至少 8 个字符'
    return
  }
  if (passwordStrengthLevel.value < minimumPasswordStrength.value) {
    passwordMessage.value = `密码强度不足，需达到「${STRENGTH_LABELS[minimumPasswordStrength.value]}」以上`
    return
  }
  savingPassword.value = true
  passwordMessage.value = ''
  try {
    await changePassword(currentPassword.value, newPassword.value)
    currentPassword.value = ''
    newPassword.value = ''
    confirmPassword.value = ''
    uni.showToast({ title: '密码已修改', icon: 'none' })
  } catch (error) {
    passwordMessage.value = friendlyError(error)
  } finally {
    savingPassword.value = false
  }
}

/** 统一错误文案转换：映射表集中在 services/errors，本页只补头像专属的状态码提示 */
function friendlyError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 413) return '头像图片过大（上限 2MB）'
    if (error.status === 415) return '仅支持 JPEG / PNG / WebP 图片'
  }
  return describeError(error, '操作失败，请稍后再试')
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
</script>

<template>
  <scroll-view class="profile-page" scroll-y :show-scrollbar="false">
    <!-- 顶部返回栏 -->
    <view class="page-head">
      <view class="back-button" @tap="goBack"><ChevronLeft :size="20" /></view>
      <text class="page-title">个人资料</text>
    </view>

    <!-- 未登录兜底（理论上由抽屉入口保证已登录，防御性处理） -->
    <view v-if="!isAuthenticated" class="login-hint">请先登录后管理个人资料</view>

    <template v-else>
      <!-- 头像区 -->
      <view class="section">
        <view class="avatar-block">
          <view class="avatar-large">
            <image v-if="hasAvatar" class="avatar-img" :src="resolveUrl(currentUser!.avatarUrl!)" mode="aspectFill" />
            <text v-else>{{ avatarLetter }}</text>
          </view>
          <view class="avatar-actions">
            <view class="pill-button" @tap="handleChangeAvatar">
              <Camera :size="16" /><text>{{ uploadingAvatar ? '上传中…' : '更换头像' }}</text>
            </view>
            <view v-if="hasAvatar" class="pill-button danger" @tap="handleDeleteAvatar">
              <Trash2 :size="16" /><text>删除</text>
            </view>
          </view>
        </view>
        <text v-if="avatarMessage" class="field-message">{{ avatarMessage }}</text>
        <text class="section-hint">支持 JPEG / PNG / WebP，不超过 2MB</text>
      </view>

      <!-- 昵称 -->
      <view class="section">
        <text class="section-title"><UserRound :size="17" /> 昵称</text>
        <input v-model="displayName" class="field-input" type="text" placeholder="展示昵称" maxlength="100" />
        <view class="save-row">
          <text v-if="nameMessage" class="field-message">{{ nameMessage }}</text>
          <view class="save-button" :class="{ disabled: savingName }" @tap="handleSaveName">
            <text>{{ savingName ? '保存中…' : '保存' }}</text>
          </view>
        </view>
      </view>

      <!-- 账号邮箱（只读） -->
      <view class="section">
        <text class="section-title">账号邮箱</text>
        <view class="readonly-field">{{ currentUser?.email }}</view>
        <text class="section-hint">邮箱为邀请注册时绑定，暂不支持修改</text>
      </view>

      <!-- 修改密码 -->
      <view class="section">
        <text class="section-title"><KeyRound :size="17" /> 修改密码</text>
        <input v-model="currentPassword" class="field-input" password placeholder="当前密码" />
        <input v-model="newPassword" class="field-input" password placeholder="新密码（至少 8 位）" />
        <view class="strength-row">
          <view class="strength-track">
            <view class="strength-fill" :style="{ width: passwordStrengthWidth, background: passwordStrengthColor }"></view>
          </view>
          <text class="strength-label">{{ STRENGTH_LABELS[passwordStrengthLevel] }}</text>
        </view>
        <input v-model="confirmPassword" class="field-input" password placeholder="确认新密码" />
        <view class="save-row">
          <text v-if="passwordMessage" class="field-message">{{ passwordMessage }}</text>
          <view class="save-button" :class="{ disabled: savingPassword }" @tap="handleSavePassword">
            <text>{{ savingPassword ? '提交中…' : '修改密码' }}</text>
          </view>
        </view>
      </view>

      <view class="bottom-space"></view>
    </template>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 个人资料页：全部使用主题令牌，四套主题下均协调 */
.profile-page { min-height: 100vh; box-sizing: border-box; background: var(--sm-page-bg); }
.page-head { display: flex; align-items: center; gap: 20rpx; padding: calc(env(safe-area-inset-top) + 28rpx) 40rpx 12rpx; }
.back-button { display: grid; place-items: center; width: 64rpx; height: 64rpx; border: 1rpx solid var(--sm-border); border-radius: 50%; background: var(--sm-surface); color: var(--sm-text-2); }
.page-title { color: var(--sm-text); font-size: 30rpx; font-weight: 800; }

.login-hint { padding: 120rpx 40rpx; color: var(--sm-text-2); font-size: 20rpx; text-align: center; }

.section { margin: 26rpx 40rpx 0; padding: 30rpx 30rpx; border: 1rpx solid var(--sm-border); border-radius: var(--sm-radius-card); background: var(--sm-surface); -webkit-backdrop-filter: blur(var(--sm-blur)); backdrop-filter: blur(var(--sm-blur)); box-shadow: var(--sm-shadow-sm); }
.section-title { display: flex; align-items: center; gap: 10rpx; margin-bottom: 22rpx; color: var(--sm-text); font-size: 21rpx; font-weight: 800; }
.section-hint { display: block; margin-top: 14rpx; color: var(--sm-text-3); font-size: 15rpx; line-height: 1.5; }

/* 头像区 */
.avatar-block { display: flex; align-items: center; gap: 30rpx; }
.avatar-large { display: grid; flex: none; place-items: center; width: 160rpx; height: 160rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; background: var(--sm-avatar-bg); color: var(--sm-cover-ink); font-size: 56rpx; font-weight: 800; overflow: hidden; }
.avatar-img { width: 100%; height: 100%; }
.avatar-actions { display: flex; flex: 1; flex-direction: column; gap: 16rpx; }
.pill-button { display: flex; align-items: center; justify-content: center; gap: 10rpx; height: 76rpx; padding: 0 30rpx; border: 1rpx solid var(--sm-border); border-radius: 38rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 19rpx; font-weight: 700; }
.pill-button.danger { border-color: var(--sm-danger-soft-border); background: var(--sm-danger-soft); color: var(--sm-danger); }

/* 表单 */
.field-input { height: 92rpx; padding: 0 24rpx; margin-bottom: 18rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 21rpx; }
.field-input::placeholder { color: var(--sm-text-3); }
.strength-row { display: flex; align-items: center; gap: 16rpx; margin-bottom: 18rpx; }
.strength-track { flex: 1; height: 10rpx; border-radius: 5rpx; background: var(--sm-surface-3); overflow: hidden; }
.strength-fill { height: 100%; border-radius: 5rpx; transition: width .2s ease, background .2s ease; }
.strength-label { flex: none; width: 64rpx; color: var(--sm-text-2); font-size: 16rpx; text-align: right; }
.readonly-field { height: 92rpx; line-height: 92rpx; padding: 0 24rpx; border: 1rpx dashed var(--sm-border-strong); border-radius: 18rpx; color: var(--sm-text-2); font-size: 21rpx; }
.save-row { display: flex; align-items: center; justify-content: space-between; gap: 20rpx; margin-top: 8rpx; }
.field-message { flex: 1; color: var(--sm-danger); font-size: 16rpx; line-height: 1.5; }
.save-button { display: flex; flex: none; align-items: center; justify-content: center; height: 76rpx; padding: 0 40rpx; border-radius: 38rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 19rpx; font-weight: 800; box-shadow: 0 8rpx 24rpx var(--sm-accent-glow); }
.save-button.disabled { opacity: .6; }

.bottom-space { height: 60rpx; }
</style>
