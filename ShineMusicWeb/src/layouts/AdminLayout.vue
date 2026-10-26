<script setup lang="ts">
/**
 * 管理后台主布局：左侧固定导航 + 顶部面包屑与账号菜单 + 右侧内容区。
 */
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox, ElMessage } from 'element-plus'
import {
  ArrowDown,
  ChatDotRound,
  Coin,
  Collection,
  Document,
  Download,
  EditPen,
  Expand,
  Fold,
  Headset,
  Key,
  Link,
  Odometer,
  Picture,
  Postcard,
  SwitchButton,
  UploadFilled,
  User,
  UserFilled,
} from '@element-plus/icons-vue'
import { useAdminStore } from '@/stores/admin'
import { currentUser, loadCurrentUser, logout } from '@/services/session'
import { resolveUrl } from '@/services/http'
import { changePassword, deleteAvatar, updateDisplayName, uploadAvatar } from '@/services/profile'

const route = useRoute()
const router = useRouter()
const store = useAdminStore()

const menuItems = [
  { path: '/dashboard', label: '仪表盘', icon: Odometer },
  { path: '/catalog', label: '曲库管理', icon: Headset },
  { path: '/import', label: '导入审核', icon: UploadFilled },
  { path: '/requests', label: '点歌需求', icon: ChatDotRound },
  { path: '/collections', label: '运营歌单', icon: Collection },
  { path: '/invitations', label: '邀请管理', icon: Postcard },
  { path: '/users', label: '用户管理', icon: UserFilled },
  { path: '/logs', label: '日志管理', icon: Document },
  { path: '/source-download', label: '音源抓歌', icon: Download },
  { path: '/playlist-import', label: '歌单解析', icon: Link },
  { path: '/storage', label: '存储治理', icon: Coin },
]

const userName = computed(() => currentUser.value?.displayName || currentUser.value?.email || '管理员')
const avatarSrc = computed(() =>
  currentUser.value?.avatarUrl ? resolveUrl(currentUser.value.avatarUrl) : undefined,
)

// 面包屑历史：每次进入新页面（按路径去重）记录一条，点击可跳回对应页面。
watch(
  () => route.path,
  () => {
    const title = typeof route.meta.title === 'string' ? route.meta.title : ''
    if (title) store.pushBreadcrumb({ path: route.path, title })
  },
  { immediate: true },
)

function goBreadcrumb(path: string) {
  if (path !== route.path) void router.push(path)
}

// ---------------------------------------------------------------------------
// 个人资料：修改昵称 / 头像 / 密码
// ---------------------------------------------------------------------------

const errorText = (e: unknown, fallback: string) => (e instanceof Error && e.message ? e.message : fallback)

// --- 修改昵称 ---
const nameDialogVisible = ref(false)
const nameSaving = ref(false)
const nameInput = ref('')

function openNameDialog() {
  nameInput.value = currentUser.value?.displayName ?? ''
  nameDialogVisible.value = true
}

async function saveDisplayName() {
  const displayName = nameInput.value.trim()
  if (!displayName) {
    ElMessage.warning('昵称不能为空')
    return
  }
  nameSaving.value = true
  try {
    await updateDisplayName(displayName)
    await loadCurrentUser()
    ElMessage.success('昵称已更新')
    nameDialogVisible.value = false
  } catch (e) {
    ElMessage.error(errorText(e, '昵称更新失败'))
  } finally {
    nameSaving.value = false
  }
}

// --- 修改头像 ---
const avatarDialogVisible = ref(false)
const avatarSaving = ref(false)
const avatarFileInput = ref<HTMLInputElement | null>(null)

function pickAvatarFile() {
  avatarFileInput.value?.click()
}

async function onAvatarFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  if (file.size > 2 * 1024 * 1024) {
    ElMessage.warning('头像文件不能超过 2MB')
    return
  }
  avatarSaving.value = true
  const objectUrl = URL.createObjectURL(file)
  try {
    await uploadAvatar(objectUrl)
    await loadCurrentUser()
    ElMessage.success('头像已更新')
    avatarDialogVisible.value = false
  } catch (e) {
    ElMessage.error(errorText(e, '头像上传失败'))
  } finally {
    URL.revokeObjectURL(objectUrl)
    avatarSaving.value = false
  }
}

async function removeAvatar() {
  avatarSaving.value = true
  try {
    await deleteAvatar()
    await loadCurrentUser()
    ElMessage.success('头像已删除')
    avatarDialogVisible.value = false
  } catch (e) {
    ElMessage.error(errorText(e, '头像删除失败'))
  } finally {
    avatarSaving.value = false
  }
}

// --- 修改密码 ---
const passwordDialogVisible = ref(false)
const passwordSaving = ref(false)
const passwordForm = ref({ current: '', next: '', confirm: '' })

function openPasswordDialog() {
  passwordForm.value = { current: '', next: '', confirm: '' }
  passwordDialogVisible.value = true
}

async function savePassword() {
  const { current, next, confirm } = passwordForm.value
  if (!current || !next) {
    ElMessage.warning('请填写当前密码与新密码')
    return
  }
  if (next !== confirm) {
    ElMessage.warning('两次输入的新密码不一致')
    return
  }
  passwordSaving.value = true
  try {
    await changePassword(current, next)
    ElMessage.success('密码已修改')
    passwordDialogVisible.value = false
  } catch (e) {
    ElMessage.error(errorText(e, '密码修改失败'))
  } finally {
    passwordSaving.value = false
  }
}

async function handleLogout() {
  try {
    await ElMessageBox.confirm('确定退出当前账号吗？', '退出登录', {
      confirmButtonText: '退出',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  await logout()
  store.resetBreadcrumbs()
  ElMessage.success('已退出登录')
  void router.replace({ name: 'login' })
}
</script>

<template>
  <el-container class="layout">
    <el-aside :width="store.sidebarCollapsed ? '64px' : '232px'" class="aside">
      <div class="brand" :class="{ mini: store.sidebarCollapsed }">
        <div class="brand-mark">♪</div>
        <div v-if="!store.sidebarCollapsed" class="brand-copy">
          <span class="brand-name">ShineMusic</span>
          <span class="brand-sub">管理后台</span>
        </div>
      </div>

      <el-menu
        :default-active="route.path"
        :collapse="store.sidebarCollapsed"
        router
        class="menu"
        background-color="transparent"
        text-color="#cbd5e1"
        active-text-color="#ffffff"
      >
        <el-menu-item v-for="item in menuItems" :key="item.path" :index="item.path">
          <el-icon><component :is="item.icon" /></el-icon>
          <template #title>{{ item.label }}</template>
        </el-menu-item>
      </el-menu>

      <div v-if="!store.sidebarCollapsed" class="aside-foot">ShineMusic Admin · v0.1</div>
    </el-aside>

    <el-container class="body">
      <el-header class="header">
        <div class="header-left">
          <el-button text class="collapse-btn" @click="store.toggleSidebar()">
            <el-icon :size="18">
              <Expand v-if="store.sidebarCollapsed" />
              <Fold v-else />
            </el-icon>
          </el-button>
          <el-breadcrumb separator="/" class="breadcrumb">
            <el-breadcrumb-item>管理后台</el-breadcrumb-item>
            <el-breadcrumb-item v-for="item in store.breadcrumbs" :key="item.path">
              <a
                class="crumb-link"
                :class="{ active: item.path === route.path }"
                @click="goBreadcrumb(item.path)"
                >{{ item.title }}</a
              >
            </el-breadcrumb-item>
          </el-breadcrumb>
        </div>

        <el-dropdown trigger="click">
          <span class="account">
            <el-avatar :size="30" class="avatar" :src="avatarSrc">
              <el-icon><User /></el-icon>
            </el-avatar>
            <span class="account-name">{{ userName }}</span>
            <el-icon class="account-arrow"><ArrowDown /></el-icon>
          </span>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item disabled>
                {{ currentUser?.email || '管理员账号' }}
              </el-dropdown-item>
              <el-dropdown-item divided @click="openNameDialog">
                <el-icon><EditPen /></el-icon>
                修改昵称
              </el-dropdown-item>
              <el-dropdown-item @click="avatarDialogVisible = true">
                <el-icon><Picture /></el-icon>
                修改头像
              </el-dropdown-item>
              <el-dropdown-item @click="openPasswordDialog">
                <el-icon><Key /></el-icon>
                修改密码
              </el-dropdown-item>
              <el-dropdown-item divided @click="handleLogout">
                <el-icon><SwitchButton /></el-icon>
                退出登录
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </el-header>

      <el-main class="main">
        <router-view />
      </el-main>
    </el-container>

    <!-- 修改昵称 -->
    <el-dialog v-model="nameDialogVisible" title="修改昵称" width="400px" append-to-body>
      <el-form label-position="top">
        <el-form-item label="展示昵称">
          <el-input v-model="nameInput" maxlength="100" show-word-limit placeholder="请输入昵称" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="nameDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="nameSaving" @click="saveDisplayName">保存</el-button>
      </template>
    </el-dialog>

    <!-- 修改头像 -->
    <el-dialog v-model="avatarDialogVisible" title="修改头像" width="400px" append-to-body>
      <div class="avatar-dialog">
        <el-avatar :size="96" :src="avatarSrc" class="avatar-preview">
          <el-icon :size="40"><User /></el-icon>
        </el-avatar>
        <p class="avatar-tip">支持 JPEG / PNG / WebP，文件不超过 2MB</p>
        <div class="avatar-actions">
          <el-button type="primary" :loading="avatarSaving" @click="pickAvatarFile">上传新头像</el-button>
          <el-button v-if="currentUser?.avatarUrl" type="danger" plain :loading="avatarSaving" @click="removeAvatar">
            删除头像
          </el-button>
        </div>
      </div>
      <input
        ref="avatarFileInput"
        type="file"
        accept="image/jpeg,image/png,image/webp"
        style="display: none"
        @change="onAvatarFileChange"
      />
    </el-dialog>

    <!-- 修改密码 -->
    <el-dialog v-model="passwordDialogVisible" title="修改密码" width="400px" append-to-body>
      <el-form label-position="top">
        <el-form-item label="当前密码">
          <el-input v-model="passwordForm.current" type="password" show-password autocomplete="current-password" />
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="passwordForm.next" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="确认新密码">
          <el-input v-model="passwordForm.confirm" type="password" show-password autocomplete="new-password" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="passwordDialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="passwordSaving" @click="savePassword">保存</el-button>
      </template>
    </el-dialog>
  </el-container>
</template>

<style scoped>
.layout {
  height: 100vh;
  background: var(--adm-page-bg);
}

.aside {
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--adm-sidebar);
  transition: width 0.2s ease;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  height: 64px;
  padding: 0 18px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}

.brand.mini {
  justify-content: center;
  padding: 0;
}

.brand-mark {
  display: grid;
  flex: none;
  place-items: center;
  width: 34px;
  height: 34px;
  border-radius: 9px;
  background: linear-gradient(135deg, #5be07a, #34a56f);
  color: #07240f;
  font-size: 19px;
  font-weight: 700;
}

.brand-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.brand-name {
  color: #f8fafc;
  font-size: 15px;
  font-weight: 700;
  letter-spacing: 0.3px;
}

.brand-sub {
  color: #7c8aa3;
  font-size: 11px;
}

.menu {
  flex: 1;
  border-right: 0;
  padding: 10px 8px;
}

.menu :deep(.el-menu-item) {
  height: 44px;
  margin-bottom: 4px;
  border-radius: 8px;
}

.menu :deep(.el-menu-item:hover) {
  background: var(--adm-sidebar-soft);
}

.menu :deep(.el-menu-item.is-active) {
  background: linear-gradient(90deg, rgba(91, 224, 122, 0.22), rgba(91, 224, 122, 0.06));
  color: #eafff0;
}

.menu :deep(.el-menu-item.is-active .el-icon) {
  color: #5be07a;
}

.aside-foot {
  padding: 14px 18px;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  color: #64748b;
  font-size: 11px;
}

.body {
  min-width: 0;
}

.header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 64px;
  padding: 0 20px;
  border-bottom: 1px solid var(--adm-border);
  background: #ffffff;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 8px;
}

.collapse-btn {
  padding: 6px;
  color: var(--adm-text-2);
}

.breadcrumb {
  max-width: 56vw;
  overflow-x: auto;
  white-space: nowrap;
}

.crumb-link {
  color: inherit;
  cursor: pointer;
  text-decoration: none;
}

.crumb-link:hover {
  color: var(--adm-primary);
}

.crumb-link.active {
  color: var(--adm-text);
  font-weight: 600;
  cursor: default;
}

.account {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  border-radius: 8px;
  color: var(--adm-text);
  cursor: pointer;
  outline: none;
}

.account:hover {
  background: #f3f5f8;
}

.avatar {
  background: var(--adm-primary-soft);
  color: var(--adm-primary);
}

.account-name {
  max-width: 160px;
  overflow: hidden;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.account-arrow {
  color: var(--adm-text-3);
  font-size: 12px;
}

.main {
  padding: 20px;
  overflow-y: auto;
}

.avatar-dialog {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 8px 0 4px;
}

.avatar-preview {
  background: var(--adm-primary-soft);
  color: var(--adm-primary);
}

.avatar-tip {
  margin: 0;
  color: var(--adm-text-3);
  font-size: 12px;
}

.avatar-actions {
  display: flex;
  gap: 12px;
}
</style>
