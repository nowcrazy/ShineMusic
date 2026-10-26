<script setup lang="ts">
/**
 * 邀请管理：发送邮箱邀请、生成注册码（CDK）、重发与撤销邀请。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, RefreshRight, Ticket, UserFilled } from '@element-plus/icons-vue'
import {
  createInvitation,
  createInvitationCode,
  getInvitations,
  resendInvitation,
  revokeInvitation,
  type Invitation,
} from '@/services/admin-invitation'
import { describeError } from '@/services/errors'
import { formatDate } from '@/utils/format'

const statusText: Record<string, string> = {
  created: '已创建',
  sent: '已发送',
  issued: '已生成注册码',
  delivery_failed: '发送失败',
  redeemed: '已注册',
  expired: '已过期',
  revoked: '已撤销',
}

const statusTone: Record<string, string> = {
  created: 'info',
  sent: 'primary',
  issued: 'success',
  delivery_failed: 'danger',
  redeemed: 'success',
  expired: 'warning',
  revoked: 'info',
}

const filterOptions = [
  { value: '', label: '全部状态' },
  { value: 'created', label: '已创建' },
  { value: 'sent', label: '已发送' },
  { value: 'issued', label: '已生成注册码' },
  { value: 'delivery_failed', label: '发送失败' },
  { value: 'redeemed', label: '已注册' },
  { value: 'expired', label: '已过期' },
  { value: 'revoked', label: '已撤销' },
]

const loading = ref(false)
const items = ref<Invitation[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const statusFilter = ref('')

const inviteEmail = ref('')
const inviting = ref(false)
const creatingCode = ref(false)
const codeDialogOpen = ref(false)
const generatedCode = ref('')

async function load() {
  loading.value = true
  try {
    const result = await getInvitations(statusFilter.value || undefined, page.value, pageSize.value)
    items.value = [...result.items]
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '邀请列表加载失败'))
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  void load()
}

async function sendInvite() {
  const email = inviteEmail.value.trim()
  if (!email) {
    ElMessage.warning('请输入受邀邮箱')
    return
  }
  inviting.value = true
  try {
    await createInvitation(email)
    ElMessage.success(`邀请已发送至 ${email}`)
    inviteEmail.value = ''
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '发送邀请失败'))
  } finally {
    inviting.value = false
  }
}

async function generateCode() {
  creatingCode.value = true
  try {
    const result = await createInvitationCode()
    generatedCode.value = result.code
    codeDialogOpen.value = true
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '生成注册码失败'))
  } finally {
    creatingCode.value = false
  }
}

async function copyCode() {
  try {
    await navigator.clipboard.writeText(generatedCode.value)
    ElMessage.success('注册码已复制')
  } catch {
    ElMessage.warning('浏览器不允许自动复制，请手动选中复制')
  }
}

async function resend(item: Invitation) {
  try {
    await resendInvitation(item.id)
    ElMessage.success('邀请已重新发送')
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '重发失败'))
  }
}

async function revoke(item: Invitation) {
  try {
    await ElMessageBox.confirm('撤销后该邀请链接立即失效，确定继续吗？', '撤销邀请', {
      confirmButtonText: '撤销',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await revokeInvitation(item.id)
    ElMessage.success('邀请已撤销')
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '撤销失败'))
  }
}

const canResend = (item: Invitation) =>
  ['created', 'sent', 'issued', 'delivery_failed'].includes(item.status)
const canRevoke = (item: Invitation) => !['redeemed', 'revoked', 'expired'].includes(item.status)

onMounted(load)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>邀请管理</h2>
        <p>通过邮箱邀请或注册码开通新账号</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
    </div>

    <div class="panel">
      <div class="create-bar">
        <el-input
          v-model="inviteEmail"
          placeholder="受邀邮箱，例如 user@example.com"
          style="max-width: 320px"
          @keyup.enter="sendInvite"
        >
          <template #prefix><el-icon><UserFilled /></el-icon></template>
        </el-input>
        <el-button type="primary" :loading="inviting" @click="sendInvite">发送邀请</el-button>
        <el-button :icon="Ticket" :loading="creatingCode" @click="generateCode">生成注册码</el-button>
      </div>

      <div class="filter-bar" style="margin-top: 16px">
        <el-select v-model="statusFilter" style="width: 160px" placeholder="全部状态" @change="search">
          <el-option v-for="option in filterOptions" :key="option.value" :label="option.label" :value="option.value" />
        </el-select>
        <span class="text-muted total">共 {{ total }} 条邀请</span>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id" style="margin-top: 12px" empty-text="暂无邀请">
        <el-table-column label="邮箱 / 类型" min-width="220">
          <template #default="{ row }">
            <div class="email-cell">
              <strong>{{ row.email || '注册码邀请' }}</strong>
              <span class="text-faint mono">{{ row.id }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="(statusTone[row.status] as any) || 'info'">
              {{ statusText[row.status] || row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="120">
          <template #default="{ row }">{{ formatDate(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="过期时间" width="120">
          <template #default="{ row }">{{ formatDate(row.expiresAt) }}</template>
        </el-table-column>
        <el-table-column label="发送时间" width="120">
          <template #default="{ row }">{{ row.sentAt ? formatDate(row.sentAt) : '—' }}</template>
        </el-table-column>
        <el-table-column label="注册时间" width="120">
          <template #default="{ row }">{{ row.redeemedAt ? formatDate(row.redeemedAt) : '—' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="150" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canResend(row)" link type="primary" :icon="RefreshRight" @click="resend(row)">
              重发
            </el-button>
            <el-button v-if="canRevoke(row)" link type="danger" @click="revoke(row)">撤销</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next"
          @current-change="load"
          @size-change="search"
        />
      </div>
    </div>

    <el-dialog v-model="codeDialogOpen" title="注册码已生成" width="460px">
      <el-result icon="success" title="请立即保存" sub-title="注册码仅显示一次，关闭后将无法再次查看" />
      <div class="code-box">
        <span class="mono code-text">{{ generatedCode }}</span>
        <el-button type="primary" plain size="small" @click="copyCode">复制</el-button>
      </div>
      <template #footer>
        <el-button type="primary" @click="codeDialogOpen = false">我已保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.create-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px;
}

.total {
  margin-left: auto;
  font-size: 13px;
}

.email-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.code-box {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 16px;
  border: 1px dashed var(--el-color-primary-light-5);
  border-radius: 8px;
  background: var(--adm-primary-soft);
}

.code-text {
  color: #145c39;
  font-size: 18px;
  font-weight: 700;
  letter-spacing: 1px;
  word-break: break-all;
}
</style>
