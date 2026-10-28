<script setup lang="ts">
/**
 * 仪表盘：聚合曲库、导入、点歌、邀请与音源任务的待办数量，并提供快捷入口。
 */
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  ChatDotRound,
  Download,
  Headset,
  Postcard,
  Refresh,
  UploadFilled,
  WarningFilled,
} from '@element-plus/icons-vue'
import { getAdminTracks } from '@/services/admin-catalog'
import { listImportItems } from '@/services/admin-import'
import { getAdminSongRequests } from '@/services/admin-requests'
import { getInvitations } from '@/services/admin-invitation'
import {
  getDownloadTasks,
  SourceProviderLabel,
  type ExternalDownloadTask,
} from '@/services/admin-source-download'
import { describeError } from '@/services/errors'
import { formatDateTime } from '@/utils/format'

const router = useRouter()
const loading = ref(false)
const stats = ref({
  tracks: 0,
  pendingReview: 0,
  pendingPublication: 0,
  openRequests: 0,
  invitations: 0,
  runningTasks: 0,
  failedTasks: 0,
})

/** 失败任务明细弹窗状态 */
const failedDialogVisible = ref(false)
const failedLoading = ref(false)
const failedTasks = ref<readonly ExternalDownloadTask[]>([])
const failedTotal = ref(0)
const failedPage = ref(1)
const failedPageSize = ref(100)

const cards = computed(() => [
  { key: 'tracks', label: '曲库曲目', value: stats.value.tracks, hint: '已发布及待整理曲目', icon: Headset, tone: 'green', to: '/catalog' },
  { key: 'pendingReview', label: '待审核导入', value: stats.value.pendingReview, hint: '等待人工审核的导入条目', icon: UploadFilled, tone: 'blue', to: '/import' },
  { key: 'pendingPublication', label: '待转公开', value: stats.value.pendingPublication, hint: '用户上传等待发布', icon: UploadFilled, tone: 'orange', to: '/import' },
  { key: 'openRequests', label: '待处理点歌', value: stats.value.openRequests, hint: '状态为待安排的需求', icon: ChatDotRound, tone: 'purple', to: '/requests' },
  { key: 'invitations', label: '邀请记录', value: stats.value.invitations, hint: '累计发出的邀请', icon: Postcard, tone: 'cyan', to: '/invitations' },
  { key: 'runningTasks', label: '音源任务', value: stats.value.runningTasks, hint: '排队 / 下载 / 待导入（不含已完成）', icon: Download, tone: 'slate', to: '/source-download' },
  { key: 'failedTasks', label: '失败任务', value: stats.value.failedTasks, hint: '音源下载失败，点击查看明细', icon: WarningFilled, tone: 'red', to: '/source-download' },
])

const shortcuts = [
  { title: '上传音频', desc: '批量导入并转码', to: '/import', icon: UploadFilled },
  { title: '审核发布', desc: '试听、修正元数据后入库', to: '/import', icon: Headset },
  { title: '处理点歌', desc: '推进或拒绝用户需求', to: '/requests', icon: ChatDotRound },
  { title: '清理存储', desc: '审计孤儿文件与空目录', to: '/storage', icon: Download },
]

async function loadStats() {
  loading.value = true
  const tasks = [
    getAdminTracks({ pageSize: 1 }),
    listImportItems('pending_review', 1, 1),
    listImportItems('pending_publication', 1, 1),
    getAdminSongRequests({ status: 'open', pageSize: 1 }),
    getInvitations(undefined, 1, 1),
    // 音源任务统计「处理中」（排队/下载/待导入）；失败单独统计，不并入待办。
    getDownloadTasks('queued', 1, 1),
    getDownloadTasks('downloading', 1, 1),
    getDownloadTasks('import_queued', 1, 1),
    getDownloadTasks('failed', 1, 1),
  ]
  const results = await Promise.allSettled(tasks)
  const total = (index: number) => {
    const result = results[index]
    return result.status === 'fulfilled' ? result.value.total : 0
  }
  stats.value = {
    tracks: total(0),
    pendingReview: total(1),
    pendingPublication: total(2),
    openRequests: total(3),
    invitations: total(4),
    runningTasks: total(5) + total(6) + total(7),
    failedTasks: total(8),
  }
  if (results.some((item) => item.status === 'rejected')) {
    const first = results.find((item) => item.status === 'rejected') as PromiseRejectedResult | undefined
    ElMessage.warning(describeError(first?.reason, '部分统计加载失败'))
  }
  loading.value = false
}

onMounted(loadStats)

/** 打开失败明细弹窗：每次打开都回到第一页重新拉取，保证数据最新。 */
async function openFailedDialog() {
  failedDialogVisible.value = true
  failedPage.value = 1
  await loadFailedTasks()
}

async function loadFailedTasks() {
  failedLoading.value = true
  try {
    const result = await getDownloadTasks('failed', failedPage.value, failedPageSize.value)
    failedTasks.value = result.items
    failedTotal.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '失败任务加载失败'))
  } finally {
    failedLoading.value = false
  }
}

function changeFailedPage(page: number) {
  failedPage.value = page
  void loadFailedTasks()
}

function changeFailedPageSize(size: number) {
  failedPageSize.value = size
  failedPage.value = 1
  void loadFailedTasks()
}

/** 普通卡片跳转对应页面；失败卡片改为打开明细弹窗。 */
function handleCardClick(card: { key: string; to: string }) {
  if (card.key === 'failedTasks') {
    void openFailedDialog()
    return
  }
  void router.push(card.to)
}

function providerLabel(provider: string) {
  return SourceProviderLabel[provider] ?? provider
}
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>仪表盘</h2>
        <p>曲库与运营数据总览</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="loadStats">刷新数据</el-button>
    </div>

    <div class="stat-grid" v-loading="loading">
      <button
        v-for="card in cards"
        :key="card.key"
        class="stat-card"
        type="button"
        @click="handleCardClick(card)"
      >
        <div class="stat-icon" :class="card.tone">
          <el-icon :size="20"><component :is="card.icon" /></el-icon>
        </div>
        <div class="stat-copy">
          <span class="stat-label">{{ card.label }}</span>
          <strong class="stat-value">{{ card.value }}</strong>
          <span class="stat-hint">{{ card.hint }}</span>
        </div>
      </button>
    </div>

    <div class="panel">
      <div class="panel-title">快捷入口</div>
      <div class="shortcut-grid">
        <button
          v-for="item in shortcuts"
          :key="item.title"
          class="shortcut"
          type="button"
          @click="router.push(item.to)"
        >
          <el-icon :size="18"><component :is="item.icon" /></el-icon>
          <div>
            <strong>{{ item.title }}</strong>
            <span>{{ item.desc }}</span>
          </div>
        </button>
      </div>
    </div>

    <el-dialog v-model="failedDialogVisible" title="失败歌曲明细" width="960px" top="6vh">
      <div class="failed-toolbar">
        <span class="text-faint">共 {{ failedTotal }} 条失败任务（仅统计 failed 状态）</span>
        <el-button size="small" :loading="failedLoading" @click="loadFailedTasks">刷新</el-button>
      </div>
      <el-table v-loading="failedLoading" :data="failedTasks" max-height="460" empty-text="暂无失败任务">
        <el-table-column label="曲名" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">{{ row.title || '—' }}</template>
        </el-table-column>
        <el-table-column label="艺人" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.artists.join(' / ') || '—' }}</template>
        </el-table-column>
        <el-table-column label="专辑" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
        </el-table-column>
        <el-table-column label="音源" width="100">
          <template #default="{ row }">{{ providerLabel(row.provider) }}</template>
        </el-table-column>
        <el-table-column label="失败原因" min-width="240" show-overflow-tooltip>
          <template #default="{ row }">{{ row.errorMessage || row.errorCode || '未知原因' }}</template>
        </el-table-column>
        <el-table-column label="更新时间" width="170">
          <template #default="{ row }">{{ formatDateTime(row.updatedAtUtc) }}</template>
        </el-table-column>
      </el-table>
      <div class="failed-pager">
        <el-pagination
          :current-page="failedPage"
          :page-size="failedPageSize"
          :page-sizes="[100, 150, 200, 500, 1000]"
          :total="failedTotal"
          layout="total, sizes, prev, pager, next"
          background
          @current-change="changeFailedPage"
          @size-change="changeFailedPageSize"
        />
      </div>
      <template #footer>
        <el-button @click="failedDialogVisible = false">关闭</el-button>
        <el-button type="primary" @click="router.push('/source-download')">去音源下载页处理</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.stat-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 14px;
}

.stat-card {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  padding: 18px;
  border: 1px solid var(--adm-border);
  border-radius: var(--adm-radius);
  background: #ffffff;
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease, transform 0.15s ease;
}

.stat-card:hover {
  border-color: #bfe3d0;
  box-shadow: 0 6px 18px rgba(16, 24, 40, 0.06);
  transform: translateY(-1px);
}

.stat-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 42px;
  height: 42px;
  border-radius: 10px;
}

.stat-icon.green { background: #e9f8f0; color: #2f9e69; }
.stat-icon.blue { background: #e8f1fe; color: #3b76d9; }
.stat-icon.orange { background: #fdf1e3; color: #c47a1d; }
.stat-icon.purple { background: #f1ecfd; color: #7b58c9; }
.stat-icon.cyan { background: #e3f7f8; color: #1e8e94; }
.stat-icon.slate { background: #eef1f6; color: #5b6b82; }
.stat-icon.red { background: #fdecec; color: #d64545; }

.stat-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.stat-label {
  color: var(--adm-text-2);
  font-size: 13px;
}

.stat-value {
  margin: 2px 0;
  color: #111827;
  font-size: 26px;
  font-weight: 700;
  line-height: 1.2;
}

.stat-hint {
  color: var(--adm-text-3);
  font-size: 12px;
}

.failed-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 10px;
}

.failed-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}

.shortcut-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
}

.shortcut {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--adm-border);
  border-radius: 9px;
  background: #fbfcfd;
  color: var(--adm-text);
  text-align: left;
  cursor: pointer;
  transition: background 0.15s ease, border-color 0.15s ease;
}

.shortcut:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--adm-primary-soft);
}

.shortcut > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.shortcut strong {
  font-size: 14px;
  font-weight: 600;
}

.shortcut span {
  color: var(--adm-text-2);
  font-size: 12px;
}
</style>
