<script setup lang="ts">
/**
 * 用户管理：检索用户、修改角色（用户/临时管理员/管理员）、重置密码、禁用/启用账号。
 *
 * 角色规则：
 * - admin 管理员：长期有效；
 * - tempadmin 临时管理员：自修改起 3 天内与管理员同权，到期后端自动降级为普通用户；
 * - 三角色互斥，修改后该用户所有端被强制下线重新登录。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Key, Lock, Plus, Refresh, RefreshLeft, Search, Unlock, UserFilled } from '@element-plus/icons-vue'
import {
  createUser,
  getAdminUsers,
  lockUser,
  resetUserPassword,
  setUserRole,
  unlockUser,
  type AdminCreateUserRequest,
  type AdminRole,
  type AdminUserItem,
} from '@/services/admin-users'
import { describeError } from '@/services/errors'
import { currentUser } from '@/services/session'
import { formatDateTime } from '@/utils/format'

const loading = ref(false)
const rows = ref<AdminUserItem[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(100)
const keyword = ref('')

const resetDialog = reactive({
  visible: false,
  user: null as AdminUserItem | null,
  password: '',
  submitting: false,
})

const roleDialog = reactive({
  visible: false,
  user: null as AdminUserItem | null,
  role: 'user' as AdminRole,
  submitting: false,
})

const roleLabels: Record<AdminRole, string> = {
  user: '用户',
  tempadmin: '临时管理员',
  admin: '管理员',
}

const createDialog = reactive({
  visible: false,
  email: '',
  userName: '',
  displayName: '',
  password: '',
  role: 'user' as AdminRole,
  submitting: false,
})

function openCreate() {
  createDialog.email = ''
  createDialog.userName = ''
  createDialog.displayName = ''
  createDialog.password = ''
  createDialog.role = 'user'
  createDialog.submitting = false
  createDialog.visible = true
}

async function submitCreate() {
  if (createDialog.email.trim().length === 0 || createDialog.userName.trim().length === 0 ||
      createDialog.displayName.trim().length === 0) {
    ElMessage.warning('邮箱、用户名、昵称均为必填')
    return
  }
  if (createDialog.password.length < 8) {
    ElMessage.warning('密码至少 8 位')
    return
  }
  createDialog.submitting = true
  const payload: AdminCreateUserRequest = {
    email: createDialog.email.trim(),
    userName: createDialog.userName.trim(),
    displayName: createDialog.displayName.trim(),
    password: createDialog.password,
    role: createDialog.role,
  }
  try {
    await createUser(payload)
    ElMessage.success(
      createDialog.role === 'admin'
        ? `已创建管理员账号 ${payload.email}`
        : createDialog.role === 'tempadmin'
          ? `已创建临时管理员 ${payload.email}（3 日后自动降级为普通用户）`
          : `已创建用户 ${payload.email}`,
    )
    createDialog.visible = false
    void load()
  } catch (error) {
    ElMessage.error(describeError(error, '创建用户失败'))
  } finally {
    createDialog.submitting = false
  }
}

async function load() {
  loading.value = true
  try {
    const data = await getAdminUsers(keyword.value, page.value, pageSize.value)
    rows.value = data.items
    total.value = data.total
  } catch (error) {
    ElMessage.error(describeError(error, '用户列表加载失败'))
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  void load()
}

function isSelf(user: AdminUserItem) {
  return currentUser.value?.id === user.id
}

/** 用户的主角色（有效角色互斥；临时管理员有效期内会同时带 admin，以 tempadmin 为准） */
function primaryRole(user: AdminUserItem): AdminRole {
  if (user.roles.includes('tempadmin')) return 'tempadmin'
  if (user.roles.includes('admin')) return 'admin'
  return 'user'
}

function roleTagType(role: AdminRole) {
  return role === 'admin' ? 'danger' : role === 'tempadmin' ? 'warning' : 'info'
}

function openRole(user: AdminUserItem) {
  roleDialog.user = user
  roleDialog.role = primaryRole(user)
  roleDialog.visible = true
}

async function submitRole() {
  const user = roleDialog.user
  if (!user) return
  roleDialog.submitting = true
  try {
    await setUserRole(user.id, roleDialog.role)
    ElMessage.success(
      roleDialog.role === 'tempadmin'
        ? `已将 ${user.email ?? user.displayName} 设为临时管理员，3 日后自动降级为普通用户`
        : `已将 ${user.email ?? user.displayName} 的角色改为「${roleLabels[roleDialog.role]}」`,
    )
    roleDialog.visible = false
    void load()
  } catch (error) {
    ElMessage.error(describeError(error, '修改角色失败'))
  } finally {
    roleDialog.submitting = false
  }
}

function openReset(user: AdminUserItem) {
  resetDialog.user = user
  resetDialog.password = ''
  resetDialog.visible = true
}

async function submitReset() {
  const user = resetDialog.user
  if (!user) return
  if (resetDialog.password.length < 8) {
    ElMessage.warning('新密码至少 8 位')
    return
  }
  resetDialog.submitting = true
  try {
    await resetUserPassword(user.id, resetDialog.password)
    ElMessage.success(`已重置 ${user.email ?? user.displayName} 的密码，其所有会话已强制下线`)
    resetDialog.visible = false
    void load()
  } catch (error) {
    ElMessage.error(describeError(error, '重置密码失败'))
  } finally {
    resetDialog.submitting = false
  }
}

async function toggleLock(user: AdminUserItem) {
  const locking = !user.isLockedOut
  try {
    await ElMessageBox.confirm(
      locking
        ? `禁用后 ${user.email ?? user.displayName} 将立即无法登录与使用 App，确认禁用？`
        : `确认解除 ${user.email ?? user.displayName} 的禁用/锁定？`,
      locking ? '禁用账号' : '解除禁用',
      { confirmButtonText: '确认', cancelButtonText: '取消', type: 'warning' },
    )
  } catch {
    return
  }
  try {
    if (locking) {
      await lockUser(user.id)
      ElMessage.success('已禁用')
    } else {
      await unlockUser(user.id)
      ElMessage.success('已解除')
    }
    void load()
  } catch (error) {
    ElMessage.error(describeError(error, '操作失败'))
  }
}

onMounted(load)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>用户管理</h2>
        <p>检索用户、添加用户（可选管理员角色）、修改角色（临时管理员 3 日后自动降级）、重置密码、禁用/启用账号</p>
      </div>
      <div class="heading-actions">
        <el-button type="primary" :icon="Plus" @click="openCreate">添加用户</el-button>
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <div class="panel">
      <div class="panel-title">
        用户列表
        <div class="filters">
          <el-input
            v-model="keyword"
            placeholder="邮箱 / 用户名 / 昵称"
            size="small"
            clearable
            style="width: 240px"
            @keyup.enter="search"
          >
            <template #prefix><el-icon><Search /></el-icon></template>
          </el-input>
          <el-button size="small" type="primary" plain @click="search">搜索</el-button>
        </div>
      </div>

      <el-table :data="rows" v-loading="loading" size="default" empty-text="没有用户">
        <el-table-column label="用户 ID" prop="id" sortable width="100" show-overflow-tooltip>
          <template #default="{ row }">{{ row.id.slice(0, 8) }}</template>
        </el-table-column>
        <el-table-column label="昵称" prop="displayName" sortable min-width="110" show-overflow-tooltip>
          <template #default="{ row }"><strong>{{ row.displayName }}</strong></template>
        </el-table-column>
        <el-table-column label="用户名" prop="userName" sortable min-width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ row.userName || '—' }}</template>
        </el-table-column>
        <el-table-column label="邮箱" prop="email" sortable min-width="180" show-overflow-tooltip>
          <template #default="{ row }">{{ row.email || '—' }}</template>
        </el-table-column>
        <el-table-column label="角色" prop="roles" sortable :sort-method="(a, b) => roleLabels[primaryRole(a)].localeCompare(roleLabels[primaryRole(b)])" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="roleTagType(primaryRole(row))">
              {{ roleLabels[primaryRole(row)] }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="临时管理员到期" prop="tempAdminUntilUtc" sortable width="160">
          <template #default="{ row }">
            <span v-if="primaryRole(row) === 'tempadmin' && row.tempAdminUntilUtc">
              {{ formatDateTime(row.tempAdminUntilUtc) }}
            </span>
            <span v-else class="text-muted">—</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" prop="isLockedOut" sortable width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isLockedOut ? 'danger' : 'success'">
              {{ row.isLockedOut ? '已禁用' : '正常' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="注册时间" prop="createdAtUtc" sortable width="160">
          <template #default="{ row }">{{ formatDateTime(row.createdAtUtc) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="300" fixed="right">
          <template #default="{ row }">
            <el-button
              size="small"
              text
              type="primary"
              :icon="UserFilled"
              :disabled="isSelf(row)"
              @click="openRole(row)"
            >
              修改角色
            </el-button>
            <el-button
              size="small"
              text
              type="primary"
              :icon="Key"
              :disabled="isSelf(row)"
              @click="openReset(row)"
            >
              重置密码
            </el-button>
            <el-button
              v-if="!isSelf(row)"
              size="small"
              text
              :type="row.isLockedOut ? 'success' : 'danger'"
              :icon="row.isLockedOut ? Unlock : Lock"
              @click="toggleLock(row)"
            >
              {{ row.isLockedOut ? '解除禁用' : '禁用' }}
            </el-button>
            <el-tag v-if="isSelf(row)" size="small" type="info" effect="plain">当前账号</el-tag>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[100, 150, 200, 500, 1000]"
          layout="total, sizes, prev, pager, next"
          @current-change="load"
          @size-change="search"
        />
      </div>
    </div>

    <el-dialog v-model="roleDialog.visible" title="修改角色" width="440px">
      <el-form label-position="top">
        <el-form-item label="用户">
          <el-input :model-value="roleDialog.user?.email ?? ''" disabled />
        </el-form-item>
        <el-form-item label="角色">
          <el-radio-group v-model="roleDialog.role">
            <el-radio value="user">用户</el-radio>
            <el-radio value="tempadmin">临时管理员</el-radio>
            <el-radio value="admin">管理员</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-alert
          v-if="roleDialog.role === 'tempadmin'"
          type="warning"
          :closable="false"
          title="临时管理员自修改时起 3 日内拥有与管理员相同的权限，3 日后自动降级为普通用户。"
        />
        <el-alert
          v-else
          type="info"
          :closable="false"
          title="修改角色后该用户的所有登录会话将被强制下线，需重新登录。"
        />
      </el-form>
      <template #footer>
        <el-button :icon="RefreshLeft" @click="roleDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="roleDialog.submitting" @click="submitRole">
          确认修改
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="resetDialog.visible" title="重置密码" width="420px">
      <el-form label-position="top">
        <el-form-item label="用户">
          <el-input :model-value="resetDialog.user?.email ?? ''" disabled />
        </el-form-item>
        <el-form-item label="新密码（至少 8 位）">
          <el-input
            v-model="resetDialog.password"
            type="password"
            show-password
            placeholder="输入新密码"
            @keyup.enter="submitReset"
          />
        </el-form-item>
        <el-alert
          type="warning"
          :closable="false"
          title="重置后该用户的所有登录会话将被强制下线，需用新密码重新登录。"
        />
      </el-form>
      <template #footer>
        <el-button :icon="RefreshLeft" @click="resetDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="resetDialog.submitting" @click="submitReset">
          确认重置
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="createDialog.visible" title="添加用户" width="480px">
      <el-form label-position="top">
        <el-form-item label="邮箱（登录凭证，必填）">
          <el-input v-model="createDialog.email" placeholder="name@example.com" autocomplete="off" />
        </el-form-item>
        <el-form-item label="用户名（登录凭证，必填）">
          <el-input v-model="createDialog.userName" placeholder="用于登录的用户名" autocomplete="off" />
        </el-form-item>
        <el-form-item label="昵称（展示名，必填）">
          <el-input v-model="createDialog.displayName" placeholder="对外展示的名称" />
        </el-form-item>
        <el-form-item label="初始密码（至少 8 位，必填）">
          <el-input
            v-model="createDialog.password"
            type="password"
            show-password
            placeholder="设置初始登录密码"
            autocomplete="new-password"
            @keyup.enter="submitCreate"
          />
        </el-form-item>
        <el-form-item label="角色">
          <el-radio-group v-model="createDialog.role">
            <el-radio value="user">用户</el-radio>
            <el-radio value="tempadmin">临时管理员</el-radio>
            <el-radio value="admin">管理员</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-alert
          v-if="createDialog.role === 'admin'"
          type="error"
          :closable="false"
          title="管理员拥有全部后台权限，请确认授予对象可信。"
        />
        <el-alert
          v-else-if="createDialog.role === 'tempadmin'"
          type="warning"
          :closable="false"
          title="临时管理员自创建起 3 日内拥有与管理员相同的权限，3 日后自动降级为普通用户。"
        />
        <el-alert
          v-else
          type="info"
          :closable="false"
          title="普通用户仅可访问前台功能；如需后台权限可稍后在列表中修改角色。"
        />
      </el-form>
      <template #footer>
        <el-button :icon="RefreshLeft" @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.submitting" @click="submitCreate">
          创建用户
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  gap: 8px;
}

.filters {
  display: flex;
  gap: 8px;
}
</style>
