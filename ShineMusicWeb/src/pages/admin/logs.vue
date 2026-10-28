<script setup lang="ts">
/**
 * 日志管理：登录审计（成功/失败、客户端与设备画像）与管理员操作审计。
 */
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Monitor, Refresh, Search } from '@element-plus/icons-vue'
import {
  ActionFilterOptions,
  ActionLabel,
  ClientTypeLabel,
  DetailKeyLabel,
  getLoginAuditLogs,
  getOperationAuditLogs,
  LoginReasonLabel,
  ResourceTypeLabel,
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
const opAction = ref('')
const opActor = ref('')

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
    const data = await getOperationAuditLogs(
      opKeyword.value,
      opPage.value,
      pageSize.value,
      opAction.value || undefined,
      opActor.value || undefined,
    )
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

/** GUID 文本：详情里的裸 Id 不再重复展示，由「对象」列呈现可读名称。 */
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

/** 系统任务（后台自动发布/匹配）的 ActorUserId 为空 Guid。 */
const SYSTEM_ACTOR = '00000000-0000-0000-0000-000000000000'

/** 是否是系统自动任务（而非真人管理员操作）。 */
function isSystemActor(row: OperationAuditItem) {
  return row.actorUserId === SYSTEM_ACTOR
}

/** 操作人显示：系统任务标记「系统」，管理员显示昵称，缺失时回退短 Id。 */
function actorLabel(row: OperationAuditItem) {
  if (isSystemActor(row)) return '系统'
  return row.actorName || row.actorUserId.slice(0, 8)
}

/** 动作中文名；未登记的动作原样显示。 */
function actionLabel(action: string) {
  return ActionLabel[action] ?? action
}

/** 资源类型中文名。 */
function resourceTypeLabel(type: string) {
  return ResourceTypeLabel[type] ?? type
}

/** 详情值格式化：数组以、连接，布尔转是/否，对象序列化。 */
function formatDetailValue(value: unknown): string {
  if (value == null) return '—'
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (Array.isArray(value)) return value.map((item) => String(item)).join('、')
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

/** 详情 JSON → 可读键值对（中文键名、过滤裸 GUID）。 */
function detailPairs(detailsJson: string | null): { key: string; label: string; value: string }[] {
  if (!detailsJson) return []
  try {
    const obj = JSON.parse(detailsJson) as Record<string, unknown>
    return Object.entries(obj)
      .filter(([, value]) => !(typeof value === 'string' && UUID_RE.test(value)))
      .map(([key, value]) => ({ key, label: DetailKeyLabel[key] ?? key, value: formatDetailValue(value) }))
  } catch {
    return [{ key: 'raw', label: '原始', value: detailsJson }]
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
            <el-select
              v-model="opAction"
              placeholder="全部动作"
              size="small"
              clearable
              style="width: 140px"
              @change="searchOperations"
            >
              <el-option v-for="opt in ActionFilterOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
            </el-select>
            <el-select
              v-model="opActor"
              placeholder="全部操作人"
              size="small"
              clearable
              style="width: 140px"
              @change="searchOperations"
            >
              <el-option label="管理员" value="admin" />
              <el-option label="系统自动" value="system" />
            </el-select>
            <el-input
              v-model="opKeyword"
              placeholder="动作 / 资源 / 操作人 / IP"
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
            <el-table-column label="操作人" width="140" show-overflow-tooltip>
              <template #default="{ row }">
                <el-tag v-if="isSystemActor(row)" size="small" type="info" effect="plain">系统</el-tag>
                <span v-else>{{ actorLabel(row) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="动作" width="170">
              <template #default="{ row }">
                <span class="action-label" :title="row.action">{{ actionLabel(row.action) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="对象" min-width="220" show-overflow-tooltip>
              <template #default="{ row }">
                <div class="object-cell">
                  <el-tag size="small" type="info" effect="plain">{{ resourceTypeLabel(row.resourceType) }}</el-tag>
                  <span class="object-name" :title="row.resourceId">
                    {{ row.resourceName || row.resourceId.slice(0, 8) }}
                  </span>
                </div>
              </template>
            </el-table-column>
            <el-table-column label="详情" min-width="240" show-overflow-tooltip>
              <template #default="{ row }">
                <span v-if="!detailPairs(row.detailsJson).length" class="text-faint">—</span>
                <span v-else class="detail-list">
                  <span v-for="pair in detailPairs(row.detailsJson)" :key="pair.key" class="detail-chip">
                    <em>{{ pair.label }}</em>{{ pair.value }}
                  </span>
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

/* === 操作日志可读化 === */
.action-label {
  color: var(--adm-text-1);
}

.object-cell {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.object-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--adm-text-1);
}

.detail-list {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 4px 10px;
}

.detail-chip {
  color: var(--adm-text-1);
  font-size: 12px;
}

.detail-chip em {
  margin-right: 3px;
  color: var(--adm-text-3);
  font-style: normal;
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
