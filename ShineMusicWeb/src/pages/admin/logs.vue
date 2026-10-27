<script setup lang="ts">
/**
 * 日志管理：登录审计（成功/失败、客户端与设备画像）与管理员操作审计。
 */
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Monitor, Refresh, Search } from '@element-plus/icons-vue'
import {
  ClientTypeLabel,
  getLoginAuditLogs,
  getOperationAuditLogs,
  LoginReasonLabel,
  type LoginAuditItem,
  type OperationAuditItem,
} from '@/services/admin-logs'
import { describeError } from '@/services/errors'
import { formatDateTime } from '@/utils/format'

/** IP 地址描述：把后端返回的带前缀 IP 字符串解析成结构化显示数据。
 *  后端可能返回：
 *    - "内网 192.168.100.109"   → kind=private
 *    - "VPN 100.110.31.122"     → kind=vpn
 *    - "本机"                    → kind=loopback
 *    - "2408:844f:1516:a41e:..." → kind=public (IPv6)
 *    - "117.128.126.30"          → kind=public (IPv4)
 *  返回对象给模板按 kind 渲染不同视觉层次。 */
function describeIp(raw: string | null | undefined) {
  if (!raw) return { kind: 'unknown' as const, label: '—', value: '', display: '—', full: '' }

  // 识别后端前缀
  let kind: 'public' | 'private' | 'vpn' | 'loopback' = 'public'
  let value = raw
  if (raw === '本机') { kind = 'loopback'; value = '' }
  else if (raw.startsWith('内网 ')) { kind = 'private'; value = raw.slice(3) }
  else if (raw.startsWith('VPN ')) { kind = 'vpn'; value = raw.slice(4) }

  // IPv6 简化：只显示前 4 段 + …（hover 看完整）
  let display = value
  if (kind === 'public' && value.includes(':') && !value.includes('.')) {
    const segments = value.split(':')
    if (segments.length > 4) display = segments.slice(0, 4).join(':') + ':…'
  }

  const labelMap: Record<typeof kind, string> = {
    public: '公网',
    private: '内网',
    vpn: 'VPN',
    loopback: '本机',
  }

  return { kind, label: labelMap[kind], value, display: display || '—', full: raw }
}

const activeTab = ref<'login' | 'operation'>('login')

// 登录日志状态
const loginLoading = ref(false)
const loginRows = ref<LoginAuditItem[]>([])
const loginTotal = ref(0)
const loginPage = ref(1)
const loginResult = ref('')
const loginKeyword = ref('')

// 操作日志状态
const opLoading = ref(false)
const opRows = ref<OperationAuditItem[]>([])
const opTotal = ref(0)
const opPage = ref(1)
const opKeyword = ref('')

const pageSize = ref(100)

async function loadLogin() {
  loginLoading.value = true
  try {
    const data = await getLoginAuditLogs(loginResult.value || undefined, loginKeyword.value, loginPage.value, pageSize.value)
    loginRows.value = data.items
    loginTotal.value = data.total
  } catch (error) {
    ElMessage.error(describeError(error, '登录日志加载失败'))
  } finally {
    loginLoading.value = false
  }
}

async function loadOperations() {
  opLoading.value = true
  try {
    const data = await getOperationAuditLogs(opKeyword.value, opPage.value, pageSize.value)
    opRows.value = data.items
    opTotal.value = data.total
  } catch (error) {
    ElMessage.error(describeError(error, '操作日志加载失败'))
  } finally {
    opLoading.value = false
  }
}

function searchLogin() {
  loginPage.value = 1
  void loadLogin()
}

/** 登录日志每页条数变化：回到第一页重新查询。 */
function changeLoginPageSize() {
  loginPage.value = 1
  void loadLogin()
}

/** 操作日志每页条数变化：回到第一页重新查询。 */
function changeOpPageSize() {
  opPage.value = 1
  void loadOperations()
}

function searchOperations() {
  opPage.value = 1
  void loadOperations()
}

function clientTag(clientType: string | null) {
  const label = ClientTypeLabel[clientType ?? 'unknown'] ?? clientType ?? '未知'
  const type = clientType === 'app' ? 'success' : clientType === 'web-admin' ? 'primary' : 'info'
  return { label, type }
}

function parseDetails(detailsJson: string | null): string {
  if (!detailsJson) return '—'
  try {
    const obj = JSON.parse(detailsJson) as Record<string, unknown>
    return Object.entries(obj)
      .map(([key, value]) => `${key}=${typeof value === 'object' ? JSON.stringify(value) : value}`)
      .join(' · ')
  } catch {
    return detailsJson
  }
}

onMounted(loadLogin)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>日志管理</h2>
        <p>登录审计（成功 / 失败、IP 与设备）与管理员操作留痕</p>
      </div>
      <el-button
        :icon="Refresh"
        :loading="activeTab === 'login' ? loginLoading : opLoading"
        @click="activeTab === 'login' ? loadLogin() : loadOperations()"
      >
        刷新
      </el-button>
    </div>

    <div class="panel">
      <el-tabs v-model="activeTab">
        <el-tab-pane label="登录日志" name="login">
          <div class="filters">
            <el-select v-model="loginResult" placeholder="全部结果" size="small" clearable style="width: 140px" @change="searchLogin">
              <el-option label="成功" value="success" />
              <el-option label="失败" value="failure" />
            </el-select>
            <el-input
              v-model="loginKeyword"
              placeholder="账号 / IP / 设备"
              size="small"
              clearable
              style="width: 240px"
              @keyup.enter="searchLogin"
            >
              <template #prefix><el-icon><Search /></el-icon></template>
            </el-input>
            <el-button size="small" type="primary" plain @click="searchLogin">搜索</el-button>
          </div>

          <el-table :data="loginRows" v-loading="loginLoading" size="default" empty-text="暂无登录日志">
            <el-table-column label="时间" width="170">
              <template #default="{ row }">{{ formatDateTime(row.createdAtUtc) }}</template>
            </el-table-column>
            <el-table-column label="账号" prop="login" min-width="180" show-overflow-tooltip />
            <el-table-column label="结果" width="130">
              <template #default="{ row }">
                <el-tag size="small" :type="row.result === 'success' ? 'success' : 'danger'">
                  {{ row.result === 'success' ? '成功' : '失败' }}
                </el-tag>
                <span v-if="row.result === 'failure'" class="reason">
                  {{ LoginReasonLabel[row.reason] ?? row.reason }}
                </span>
              </template>
            </el-table-column>
            <el-table-column label="客户端" width="110">
              <template #default="{ row }">
                <el-tag size="small" :type="clientTag(row.clientType).type" effect="plain">
                  {{ clientTag(row.clientType).label }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="设备" min-width="180" show-overflow-tooltip>
              <template #default="{ row }">{{ row.deviceInfo || '—' }}</template>
            </el-table-column>
            <el-table-column label="IP" width="170">
              <template #default="{ row }">
                <template v-if="describeIp(row.ip).kind === 'public'">
                  <span class="mono ip-public" :title="describeIp(row.ip).full">{{ describeIp(row.ip).display }}</span>
                </template>
                <template v-else-if="describeIp(row.ip).kind !== 'unknown'">
                  <span class="ip-private" :title="describeIp(row.ip).full">
                    <el-tag size="small" effect="plain" type="info" class="ip-kind-tag">{{ describeIp(row.ip).label }}</el-tag>
                    <span v-if="describeIp(row.ip).value" class="mono ip-value">{{ describeIp(row.ip).value }}</span>
                  </span>
                </template>
                <span v-else class="text-faint">—</span>
              </template>
            </el-table-column>
          </el-table>

          <div class="pagination">
            <el-pagination
              v-model:current-page="loginPage"
              v-model:page-size="pageSize"
              :total="loginTotal"
              :page-sizes="[100, 150, 200, 500, 1000]"
              layout="total, sizes, prev, pager, next"
              @current-change="loadLogin"
              @size-change="changeLoginPageSize"
            />
          </div>
        </el-tab-pane>

        <el-tab-pane name="operation" lazy>
          <template #label>
            <span><el-icon style="vertical-align: -2px"><Monitor /></el-icon> 操作日志</span>
          </template>
          <div class="filters">
            <el-input
              v-model="opKeyword"
              placeholder="动作 / 资源 / IP"
              size="small"
              clearable
              style="width: 240px"
              @keyup.enter="searchOperations"
            >
              <template #prefix><el-icon><Search /></el-icon></template>
            </el-input>
            <el-button size="small" type="primary" plain @click="searchOperations">搜索</el-button>
          </div>

          <el-table :data="opRows" v-loading="opLoading" size="default" empty-text="暂无操作日志">
            <el-table-column label="时间" width="170">
              <template #default="{ row }">{{ formatDateTime(row.createdAtUtc) }}</template>
            </el-table-column>
            <el-table-column label="操作人" width="150" show-overflow-tooltip>
              <template #default="{ row }">{{ row.actorName || row.actorUserId.slice(0, 8) }}</template>
            </el-table-column>
            <el-table-column label="动作" width="200">
              <template #default="{ row }">
                <span class="mono">{{ row.action }}</span>
              </template>
            </el-table-column>
            <el-table-column label="详情" min-width="220" show-overflow-tooltip>
              <template #default="{ row }">{{ parseDetails(row.detailsJson) }}</template>
            </el-table-column>
            <el-table-column label="客户端" width="110">
              <template #default="{ row }">
                <el-tag size="small" :type="clientTag(row.clientType).type" effect="plain">
                  {{ clientTag(row.clientType).label }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="IP" width="170">
              <template #default="{ row }">
                <template v-if="describeIp(row.ip).kind === 'public'">
                  <span class="mono ip-public" :title="describeIp(row.ip).full">{{ describeIp(row.ip).display }}</span>
                </template>
                <template v-else-if="describeIp(row.ip).kind !== 'unknown'">
                  <span class="ip-private" :title="describeIp(row.ip).full">
                    <el-tag size="small" effect="plain" type="info" class="ip-kind-tag">{{ describeIp(row.ip).label }}</el-tag>
                    <span v-if="describeIp(row.ip).value" class="mono ip-value">{{ describeIp(row.ip).value }}</span>
                  </span>
                </template>
                <span v-else class="text-faint">—</span>
              </template>
            </el-table-column>
          </el-table>

          <div class="pagination">
            <el-pagination
              v-model:current-page="opPage"
              v-model:page-size="pageSize"
              :total="opTotal"
              :page-sizes="[100, 150, 200, 500, 1000]"
              layout="total, sizes, prev, pager, next"
              @current-change="loadOperations"
              @size-change="changeOpPageSize"
            />
          </div>
        </el-tab-pane>
      </el-tabs>
    </div>
  </div>
</template>

<style scoped>
.filters {
  display: flex;
  gap: 8px;
  margin-bottom: 14px;
}

.reason {
  margin-left: 6px;
  color: var(--adm-text-2);
  font-size: 12px;
}

/* === IP 显示分层 === */
.ip-public {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-weight: 500;
  color: var(--adm-text-1);
  letter-spacing: 0.01em;
}

.ip-private {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--adm-text-3);
}

.ip-kind-tag {
  opacity: 0.7;
  font-size: 11px;
  padding: 0 4px;
  height: 18px;
  line-height: 16px;
}

.ip-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 12px;
  color: var(--adm-text-3);
  opacity: 0.85;
}
</style>
