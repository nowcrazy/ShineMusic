<script setup lang="ts">
/**
 * 头像抽屉（QQ 式左侧滑出）：点击头像后滑出的个人菜单。
 * 内容：资料摘要（头像/昵称/邮箱/角色徽章）+「账号」组（个人资料；主题外观/语言——内嵌展开直接切换；服务器地址——
 *       后端地址查看/修改，保存即生效，APK 端地址失联时的自救入口）
 *       +「管理员」组（导入审核/曲目维护/歌单管理/需求管理/邀请管理，仅管理员）+ 退出登录。
 * 高频功能（消息/播放历史/收藏/关注）已移至首页 tabbar 快捷面板与「我的」页功能入口，此处不再重复。
 * 触发：首页顶部头像与“我的”tab 资料卡（未登录时走登录页，不打开抽屉）。
 * 面板常驻 DOM 以支持滑入/滑出过渡，关闭时 pointer-events 置空不影响页面交互。
 * 对应页面：pages/profile（个人资料）；退出走 services/session（清空本地收藏/歌单/曲库后重启首页）。
 */
import { computed, ref } from 'vue'
import { AudioLines, Check, ChevronRight, Inbox, Languages, LogOut, Mail, Palette, Server, ShieldCheck, Upload, UserCog, X } from 'lucide-vue-next'
import LanguageSwitcher from './LanguageSwitcher.vue'
import { currentUser, isAdmin, logout } from '@/services/session'
import { resetLibrary } from '@/services/library'
import { resetFeedback } from '@/services/feedback'
import { resetNotifications } from '@/services/notifications'
import { resetMyLibrary } from '@/services/user-library'
import { applyTheme, currentTheme, THEMES, type ThemeId } from '@/services/theme'
import { currentApiBase, getApiBaseOverride, resolveUrl, setApiBaseOverride } from '@/services/http'
import { useI18n } from 'vue-i18n'

// 文案多语言
const { t } = useI18n()

// 显隐由父组件 v-model 控制
const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

// 头像首字母：显示名首字符大写，缺省 S（无上传头像时的回退展示）
const avatarLetter = computed(() => currentUser.value?.displayName?.slice(0, 1).toUpperCase() || 'S')

// 主题区块内嵌展开状态：点击「主题外观」行切换，展开后直接在下方点选，无需跳页
const themeExpanded = ref(false)

// 语言区块内嵌展开状态：点击「语言」行切换，展开后点选语言立即生效并持久化
const langExpanded = ref(false)

// 退出请求进行中（防止 Modal 回调重复提交）
const loggingOut = ref(false)

// 服务器地址：当前生效地址（用户自定义 > 构建期内置），设置抽屉里查看/修改
const serverBase = ref(currentApiBase())

/**
 * 修改服务器地址：弹窗输入 → 保存立即生效并持久化 → 测试连通性。
 * 留空确认即清除自定义、恢复 APK 内置地址；连不通也会保存，便于提前填好新地址。
 */
function editServerBase() {
  uni.showModal({
    title: t('drawer.serverAddress'),
    editable: true,
    content: getApiBaseOverride(),
    placeholderText: t('drawer.serverPlaceholder'),
    confirmText: t('drawer.serverSaveTest'),
    success: ({ confirm, content }) => {
      if (!confirm) return
      setApiBaseOverride(content ?? '')
      serverBase.value = currentApiBase()
      if (!getApiBaseOverride()) {
        uni.showToast({ title: t('drawer.serverRestored'), icon: 'none' })
        return
      }
      uni.showLoading({ title: t('drawer.serverTesting') })
      uni.request({
        url: `${currentApiBase()}/health`,
        method: 'GET',
        timeout: 8000,
        complete: (result) => {
          uni.hideLoading()
          const code = (result as { statusCode?: number }).statusCode
          uni.showToast({
            title: code && code >= 200 && code < 300 ? t('drawer.serverConnected') : t('drawer.serverNotConnected'),
            icon: 'none',
          })
        },
      })
    },
  })
}

/** 关闭抽屉 */
function close() {
  emit('update:modelValue', false)
}

/** 选中某个主题（点击即生效并持久化，全站 CSS 令牌变量即时换肤） */
function selectTheme(id: ThemeId) {
  if (currentTheme.value === id) return
  applyTheme(id)
  const switchedTheme = THEMES.find((theme) => theme.id === id)
  uni.showToast({ title: t('drawer.themeSwitched', { name: switchedTheme ? t(switchedTheme.nameKey) : '' }), icon: 'none' })
}

/** 跳转个人资料页 */
function goProfile() {
  close()
  uni.navigateTo({ url: '/pages/profile/index' })
}

/** 跳转管理员曲库导入审核页 */
function goImportManagement() {
  close()
  uni.navigateTo({ url: '/pages/admin/import' })
}

/** 跳转管理员曲目维护页 */
function goCatalogManagement() {
  close()
  uni.navigateTo({ url: '/pages/admin/catalog' })
}

/** 跳转管理员歌单管理页 */
function goCollectionManagement() {
  close()
  uni.navigateTo({ url: '/pages/admin/collections' })
}

/** 跳转管理员邀请管理页 */
function goInvitationManagement() {
  close()
  uni.navigateTo({ url: '/pages/admin/invitation' })
}

/** 跳转管理员需求管理页 */
function goRequestManagement() {
  close()
  uni.navigateTo({ url: '/pages/admin/requests' })
}

/**
 * 退出登录：二次确认后调用登出接口、清空本地收藏/歌单/曲库/反馈状态并重启到首页。
 */
function handleLogout() {
  uni.showModal({
    title: t('drawer.logout'),
    content: t('drawer.logoutKeepData'),
    confirmText: t('drawer.logoutConfirmBtn'),
    success: async ({ confirm }) => {
      if (!confirm || loggingOut.value) return
      loggingOut.value = true
      try {
        await logout()
        resetLibrary()
        resetMyLibrary()
        resetFeedback()
        resetNotifications()
        uni.showToast({ title: t('drawer.loggedOut'), icon: 'none' })
        setTimeout(() => uni.reLaunch({ url: '/pages/home/index' }), 300)
      } finally {
        loggingOut.value = false
      }
    },
  })
}
</script>

<template>
  <view class="drawer-root" :class="{ open: props.modelValue }">
    <!-- 遮罩：点击关闭 -->
    <view class="drawer-mask" @tap="close"></view>

    <!-- 抽屉面板 -->
    <view class="drawer-panel">
      <!-- 头部：品牌 + 关闭 -->
      <view class="drawer-head">
        <view class="drawer-brand">
          <view class="drawer-mark"><AudioLines :size="17" :stroke-width="2.7" /></view>
          <text class="drawer-brand-name">shine.</text>
        </view>
        <view class="drawer-close" @tap="close"><X :size="19" /></view>
      </view>

      <!-- 资料摘要 -->
      <view class="drawer-profile">
        <view class="drawer-avatar">
          <image v-if="currentUser?.avatarUrl" class="drawer-avatar-img" :src="resolveUrl(currentUser.avatarUrl)" mode="aspectFill" />
          <text v-else>{{ avatarLetter }}</text>
        </view>
        <view class="drawer-info">
          <text class="drawer-user">{{ currentUser?.displayName }}</text>
          <text class="drawer-mail">{{ currentUser?.email }}</text>
          <view class="drawer-roles">
            <text class="drawer-role">{{ t('drawer.listener') }}</text>
            <view v-if="isAdmin" class="drawer-role admin"><ShieldCheck :size="12" /><text>{{ t('drawer.admin') }}</text></view>
          </view>
        </view>
      </view>

      <!-- 功能入口：「账号」组（个人资料 + 主题外观内嵌展开）+「管理员」组（仅管理员） -->
      <scroll-view class="drawer-menu-scroll" scroll-y :show-scrollbar="false">
        <view class="drawer-menu">
          <text class="drawer-group">{{ t('drawer.groupAccount') }}</text>
          <view class="drawer-item" @tap="goProfile">
            <view class="drawer-icon profile-icon"><UserCog :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.profile') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>

          <!-- 主题外观：点击展开内嵌主题选择，选中即切换，不再跳转独立页面 -->
          <view class="drawer-item" @tap="themeExpanded = !themeExpanded">
            <view class="drawer-icon theme-icon"><Palette :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.theme') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" :class="{ rotated: themeExpanded }" />
          </view>
          <view v-if="themeExpanded" class="theme-inline">
            <view
              v-for="theme in THEMES"
              :key="theme.id"
              class="theme-chip"
              :class="{ active: currentTheme === theme.id }"
              @tap="selectTheme(theme.id)"
            >
              <view class="theme-chip-swatch" :style="{ background: theme.swatch.bg }">
                <view class="theme-chip-dot" :style="{ background: theme.swatch.accent }"></view>
                <view class="theme-chip-bar" :style="{ background: theme.swatch.text }"></view>
              </view>
              <view class="theme-chip-copy">
                <text class="theme-chip-name">{{ t(theme.nameKey) }}</text>
                <text class="theme-chip-tone">{{ t(theme.toneKey) }}</text>
              </view>
              <view v-if="currentTheme === theme.id" class="theme-chip-check"><Check :size="13" /></view>
            </view>
          </view>

          <!-- 语言：点击展开内嵌语言切换（简体中文/English/日本語），选择立即生效并持久化 -->
          <view class="drawer-item" @tap="langExpanded = !langExpanded">
            <view class="drawer-icon lang-icon"><Languages :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.language') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" :class="{ rotated: langExpanded }" />
          </view>
          <view v-if="langExpanded" class="lang-inline">
            <LanguageSwitcher :show-check="true" layout="fill" />
          </view>

          <!-- 服务器地址：查看/修改后端地址（保存即生效；APK 端地址失联时自救） -->
          <view class="drawer-item" @tap="editServerBase">
            <view class="drawer-icon server-icon"><Server :size="19" /></view>
            <view class="drawer-item-copy">
              <text class="drawer-item-title">{{ t('drawer.serverAddress') }}</text>
              <text class="drawer-item-sub">{{ serverBase || t('drawer.notConfigured') }}</text>
            </view>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>

          <template v-if="isAdmin">
            <text class="drawer-group">{{ t('drawer.admin') }}</text>
            <view class="drawer-item" @tap="goRequestManagement">
            <view class="drawer-icon admin-icon"><Inbox :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.requestManagement') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>
          <view class="drawer-item" @tap="goCollectionManagement">
            <view class="drawer-icon collection-icon"><AudioLines :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.playlistManagement') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>
          <view class="drawer-item" @tap="goImportManagement">
            <view class="drawer-icon import-icon"><Upload :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.importReview') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>
          <view class="drawer-item" @tap="goCatalogManagement">
            <view class="drawer-icon admin-icon"><ShieldCheck :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.trackMaintenance') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>
          <view class="drawer-item" @tap="goInvitationManagement">
            <view class="drawer-icon invite-icon"><Mail :size="19" /></view>
            <text class="drawer-item-title">{{ t('drawer.invitationManagement') }}</text>
            <ChevronRight :size="16" class="drawer-item-chevron" />
          </view>
          </template>
        </view>
      </scroll-view>

      <!-- 底部：退出登录 -->
      <view class="drawer-foot">
        <view class="drawer-logout" @tap="handleLogout"><LogOut :size="18" /><text>{{ t('drawer.logout') }}</text></view>
      </view>
    </view>
  </view>
</template>

<style lang="scss" scoped>
/* 头像抽屉（QQ 式左侧滑出 · 玻璃质感） */
.drawer-root { position: fixed; inset: 0; z-index: 100; pointer-events: none; }
.drawer-root.open { pointer-events: auto; }
.drawer-mask { position: absolute; inset: 0; background: rgba(8, 10, 14, .52); opacity: 0; transition: opacity .25s ease; }
.drawer-root.open .drawer-mask { opacity: 1; }
.drawer-panel { position: absolute; top: 0; bottom: 0; left: 0; display: flex; width: 640rpx; flex-direction: column; padding: calc(env(safe-area-inset-top) + 30rpx) 40rpx calc(env(safe-area-inset-bottom) + 30rpx); background: var(--sm-page-bg); box-shadow: 16rpx 0 54rpx rgba(0, 0, 0, .38); transform: translateX(-103%); transition: transform .3s cubic-bezier(.32, .72, 0, 1); box-sizing: border-box; }
.drawer-root.open .drawer-panel { transform: translateX(0); }
.drawer-head { display: flex; align-items: center; justify-content: space-between; }
.drawer-brand { display: flex; align-items: center; gap: 11rpx; }
.drawer-mark { display: grid; place-items: center; width: 40rpx; height: 40rpx; border-radius: 13rpx; background: var(--sm-surface-3); border: 1rpx solid var(--sm-border-strong); color: var(--sm-accent); }
.drawer-brand-name { font-family: 'Manrope', sans-serif; font-size: 32rpx; font-weight: 800; color: var(--sm-text); }
.drawer-brand-name span { color: var(--sm-accent); }
.drawer-close { display: grid; place-items: center; width: 60rpx; height: 60rpx; border-radius: 50%; background: var(--sm-surface-3); color: var(--sm-text-2); }
.drawer-profile { display: flex; align-items: center; gap: 22rpx; margin-top: 44rpx; padding: 28rpx 26rpx; border: 1rpx solid var(--sm-border); border-radius: 22rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.drawer-avatar { display: grid; flex: none; place-items: center; width: 108rpx; height: 108rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 50%; background: var(--sm-avatar-bg); color: var(--sm-cover-ink); font-size: 40rpx; font-weight: 800; overflow: hidden; }
.drawer-avatar-img { width: 100%; height: 100%; }
.drawer-info { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.drawer-user { overflow: hidden; color: var(--sm-text); font-size: 27rpx; font-weight: 800; text-overflow: ellipsis; white-space: nowrap; }
.drawer-mail { overflow: hidden; color: var(--sm-text-2); font-size: 16rpx; text-overflow: ellipsis; white-space: nowrap; }
.drawer-roles { display: flex; gap: 10rpx; margin-top: 4rpx; }
.drawer-role { display: flex; align-items: center; gap: 5rpx; padding: 5rpx 14rpx; border: 1rpx solid var(--sm-border); border-radius: 30rpx; background: var(--sm-surface); color: var(--sm-text-2); font-size: 14rpx; font-weight: 700; }
.drawer-role.admin { border-color: var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }
/* 菜单区可滚动（主题展开后内容变长，底部退出按钮保持吸底） */
.drawer-menu-scroll { flex: 1; min-height: 0; margin-top: 30rpx; }
.drawer-menu { display: flex; flex-direction: column; }
.drawer-item { display: flex; align-items: center; gap: 18rpx; padding: 24rpx 8rpx; border-bottom: 1rpx solid var(--sm-divider); }
.drawer-icon { display: grid; flex: none; place-items: center; width: 66rpx; height: 66rpx; border-radius: 18rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.drawer-icon.profile-icon { background: var(--sm-accent-soft); color: var(--sm-accent); }
.drawer-icon.theme-icon { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
.drawer-icon.collection-icon { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.drawer-icon.import-icon { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.drawer-icon.admin-icon { background: var(--sm-accent-soft); color: var(--sm-accent); }
.drawer-icon.invite-icon { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
/* 分组标题：「账号」/「管理员」 */
.drawer-group { padding: 10rpx 8rpx 4rpx; color: var(--sm-text-3); font-size: 18rpx; font-weight: 800; letter-spacing: .12em; }
.drawer-item-title { flex: 1; color: var(--sm-text); font-size: 22rpx; font-weight: 600; }
.drawer-item-chevron { flex: none; color: var(--sm-text-3); transition: transform .2s ease; }
.drawer-item-chevron.rotated { transform: rotate(90deg); }
.drawer-icon.server-icon { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.drawer-icon.lang-icon { background: var(--sm-accent-3-soft); color: var(--sm-accent-3); }
/* 内嵌语言切换：与主题色卡同款内嵌容器 */
.lang-inline { padding: 6rpx 8rpx 20rpx; }
/* 服务器地址行：标题 + 当前生效地址副标题双行展示 */
.drawer-item-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 2rpx; }
.drawer-item-copy .drawer-item-title { flex: none; }
.drawer-item-sub { overflow: hidden; color: var(--sm-text-3); font-size: 16rpx; text-overflow: ellipsis; white-space: nowrap; }

/* 内嵌主题选择：2 列色卡，选中态高亮描边 */
.theme-inline { display: grid; grid-template-columns: 1fr 1fr; gap: 14rpx; padding: 6rpx 8rpx 20rpx; }
.theme-chip { display: flex; align-items: center; gap: 12rpx; padding: 14rpx; border: 1rpx solid var(--sm-border); border-radius: 18rpx; background: var(--sm-surface); transition: border-color .2s ease; }
.theme-chip.active { border-color: var(--sm-accent); box-shadow: 0 0 0 2rpx var(--sm-accent-soft-border); }
.theme-chip-swatch { position: relative; display: flex; flex: none; align-items: flex-end; width: 64rpx; height: 64rpx; padding: 10rpx; border: 1rpx solid var(--sm-border); border-radius: 12rpx; overflow: hidden; }
.theme-chip-dot { width: 14rpx; height: 14rpx; border-radius: 50%; }
.theme-chip-bar { position: absolute; top: 10rpx; right: 10rpx; width: 22rpx; height: 6rpx; border-radius: 3rpx; opacity: .55; }
.theme-chip-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 4rpx; }
.theme-chip-name { overflow: hidden; color: var(--sm-text); font-size: 17rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.theme-chip-tone { color: var(--sm-text-3); font-size: 12rpx; }
.theme-chip-check { display: grid; flex: none; place-items: center; width: 34rpx; height: 34rpx; border-radius: 50%; background: var(--sm-accent); color: var(--sm-accent-ink); }

.drawer-foot { padding-top: 24rpx; }
.drawer-logout { display: flex; align-items: center; justify-content: center; gap: 12rpx; height: 92rpx; border: 1rpx solid var(--sm-danger-soft-border); border-radius: 46rpx; background: var(--sm-danger-soft); color: var(--sm-danger); font-size: 20rpx; font-weight: 700; }
</style>
