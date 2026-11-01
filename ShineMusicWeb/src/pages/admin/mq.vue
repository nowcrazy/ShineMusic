<script setup lang="ts">
/**
 * 消息队列监控：查看 RabbitMQ 各队列的积压、处理中、在线消费者与收发速率，支持 10s 自动刷新。
 * 数据来自后端转发的 RabbitMQ Management HTTP API；插件不可达时提示 502 原因。
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { getMessageQueueTasks, getMessageQueues } from '@/services/admin-mq'
import { describeError } from '@/services/errors'
import type { ApiMessageQueueStatus, ApiMessageQueueTask, ApiMessageQueueTaskList } from '@/types/api.generated'

const loading = ref(false)
const queues = ref<ApiMessageQueueStatus[]>([])
const loadError = ref('')
const tasksLoading = ref(false)
const taskError = ref('')
const taskList = ref<ApiMessageQueueTaskList | null>(null)
const taskQueue = ref('shinemusic.downloads')
const taskState = ref<'all' | 'waiting' | 'processing'>('all')
const taskPage = ref(1)
const autoRefresh = ref(true)
const lastUpdated = ref('')
let timer: ReturnType<typeof setInterval> | null = null

const totalReady = computed(() => queues.value.reduce((sum, queue) => sum + queue.messagesReady, 0))
const totalUnacked = computed(() => queues.value.reduce((sum, queue) => sum + queue.messagesUnacknowledged, 0))
/** 消费模式概述：直接回答“串行还是并行”。 */
const modeSummary = computed(() => {
  if (!queues.value.length) return ''
  const parallel = queues.value.filter((queue) => queue.mode === 'parallel')
  const serial = queues.value.filter((queue) => queue.mode === 'serial')
  if (parallel.length) {
    return `部分队列并行（${parallel.map((queue) => `${queue.name} ×${queue.concurrency}`).join('、')}），其余队列串行`
  }
  if (serial.length) return '全部串行：每个队列同一时刻只处理 1 条任务，其余按顺序排队'
  return '当前没有在线消费者，消息只会积压不会被执行'
})

async function load(showLoading = true) {
  if (showLoading) loading.value = true
  try {
    queues.value = await getMessageQueues()
    loadError.value = ''
    lastUpdated.value = new Date().toLocaleTimeString()
  } catch (error) {
    loadError.value = describeError(error, '队列状态获取失败')
    if (showLoading) ElMessage.error(loadError.value)
  } finally {
    loading.value = false
  }
  await loadTasks()
}

async function loadTasks() {
  tasksLoading.value = true
  try {
    taskList.value = await getMessageQueueTasks(taskQueue.value, taskState.value, 50, taskPage.value)
    taskError.value = ''
  } catch (error) {
    taskError.value = describeError(error, '队列任务明细获取失败')
  } finally {
    tasksLoading.value = false
  }
}

function applyAutoRefresh(enabled: boolean) {
  if (enabled) {
    timer ??= setInterval(() => void load(false), 10_000)
  } else if (timer) {
    clearInterval(timer)
    timer = null
  }
}

function onAutoRefreshChange(value: string | number | boolean) {
  applyAutoRefresh(Boolean(value))
}

function formatRate(rate: number | null | undefined) {
  return rate == null ? '—' : `${rate.toFixed(1)}/s`
}

function stateTagType(state: string) {
  return state === 'running' ? 'success' : 'danger'
}

function stateText(state: string) {
  if (state === 'running') return '运行中'
  if (state === 'down') return '已停止'
  return state
}

/** 消费模式文案：串行 / 并行 ×N / 无消费者。 */
function modeText(queue: ApiMessageQueueStatus) {
  if (queue.mode === 'offline') return '无消费者'
  return queue.mode === 'serial' ? '串行' : `并行 ×${queue.concurrency}`
}

function modeTagType(queue: ApiMessageQueueStatus) {
  if (queue.mode === 'offline') return 'danger'
  return queue.mode === 'serial' ? 'info' : 'warning'
}

function taskStateText(state: string) {
  if (state === 'waiting') return '等待中'
  if (state === 'processing') return '处理中'
  return state
}

function taskStatusText(status: string) {
  const labels: Record<string, string> = {
    queued: '排队中',
    downloading: '下载中',
    uploaded: '已上传，等待处理',
    validating: '校验中',
    parsing_metadata: '解析元数据',
    transcoding: '转码中',
  }
  return labels[status] ?? status
}

function formatTaskDate(value: string) {
  return new Date(value).toLocaleString()
}

function formatFileSize(value: number | null | undefined) {
  if (value == null) return '—'
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / (1024 * 1024)).toFixed(1)} MB`
}

function taskTitle(task: ApiMessageQueueTask) {
  return task.originalFileName || task.title || '未识别标题'
}

function taskProviderFormat(task: ApiMessageQueueTask) {
  return task.provider || [task.container, task.codec].filter(Boolean).join(' / ') || '—'
}

function changeTaskFilter() {
  taskPage.value = 1
  void loadTasks()
}

function changeTaskPage(page: number) {
  taskPage.value = page
  void loadTasks()
}

onMounted(() => {
  void load()
  applyAutoRefresh(autoRefresh.value)
})
onUnmounted(() => applyAutoRefresh(false))
</script>

<template>
  <div class="mq-page">
    <div class="panel">
      <div class="panel-title">
        RabbitMQ 队列
        <span class="text-faint summary">
          共 {{ queues.length }} 个队列 · 待消费 {{ totalReady }} · 处理中 {{ totalUnacked }}
          <template v-if="lastUpdated"> · 更新于 {{ lastUpdated }}</template>
        </span>
        <span class="actions">
          <el-switch v-model="autoRefresh" active-text="自动刷新" @change="onAutoRefreshChange" />
          <el-button :icon="Refresh" :loading="loading" size="small" @click="load()">刷新</el-button>
        </span>
      </div>

      <el-alert
        v-if="loadError"
        type="error"
        :closable="false"
        show-icon
        title="队列状态获取失败"
        :description="loadError"
        style="margin-bottom: 12px"
      />

      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="mode-tip"
        :title="`消费模式：${modeSummary}`"
        description="队列之间互相独立：下载队列与导入队列可以同时各处理自己的任务。队列内部并行度 = 在线消费者数 × 预取数，预取为 1 时严格逐条串行；调整配置 MaxConcurrentDownloads / MaxConcurrentJobs（1~4）可提高单队列并行度。"
      />

      <el-table v-loading="loading" :data="queues" row-key="name" empty-text="暂无队列信息">
        <el-table-column label="队列名" prop="name" sortable min-width="230">
          <template #default="{ row }">
            <div class="title-cell">
              <strong class="queue-name">{{ row.name }}</strong>
              <span class="text-faint">{{ row.role }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" prop="state" sortable width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="stateTagType(row.state)">{{ stateText(row.state) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="排队中" prop="messagesReady" sortable width="90" align="right">
          <template #default="{ row }">
            <strong :class="{ 'text-danger': row.messagesReady > 0 }">{{ row.messagesReady }}</strong>
          </template>
        </el-table-column>
        <el-table-column label="处理中" prop="messagesUnacknowledged" sortable width="90" align="right">
          <template #default="{ row }">{{ row.messagesUnacknowledged }}</template>
        </el-table-column>
        <el-table-column label="消费者" prop="consumers" sortable width="90" align="right">
          <template #default="{ row }">
            <strong :class="{ 'text-danger': row.consumers === 0 }">{{ row.consumers }}</strong>
          </template>
        </el-table-column>
        <el-table-column label="预取" prop="prefetch" sortable width="80" align="right">
          <template #default="{ row }">{{ row.prefetch }}</template>
        </el-table-column>
        <el-table-column label="消费模式" prop="mode" sortable width="120" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="modeTagType(row)" effect="plain">{{ modeText(row) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="并行度" prop="concurrency" sortable width="90" align="right">
          <template #default="{ row }">{{ row.concurrency }}</template>
        </el-table-column>
        <el-table-column label="发布速率" prop="publishRate" sortable width="100" align="right">
          <template #default="{ row }">{{ formatRate(row.publishRate) }}</template>
        </el-table-column>
        <el-table-column label="投递速率" prop="deliverRate" sortable width="100" align="right">
          <template #default="{ row }">{{ formatRate(row.deliverRate) }}</template>
        </el-table-column>
      </el-table>
    </div>

    <div class="panel task-panel">
      <div class="panel-title">
        队列任务明细
        <span class="text-faint summary">
          {{ taskList ? `共 ${taskList.total} 条` : '' }}
        </span>
        <span class="actions task-filters">
          <el-select v-model="taskQueue" size="small" style="width: 180px" @change="changeTaskFilter">
            <el-option label="音源下载" value="shinemusic.downloads" />
            <el-option label="曲库导入" value="shinemusic.imports" />
          </el-select>
          <el-select v-model="taskState" size="small" style="width: 120px" @change="changeTaskFilter">
            <el-option label="全部状态" value="all" />
            <el-option label="等待中" value="waiting" />
            <el-option label="处理中" value="processing" />
          </el-select>
        </span>
      </div>

      <el-alert
        v-if="taskError"
        type="error"
        :closable="false"
        show-icon
        title="队列任务明细获取失败"
        :description="taskError"
        style="margin-bottom: 12px"
      />
      <el-alert
        type="info"
        :closable="false"
        show-icon
        class="mode-tip"
        title="明细来自 PostgreSQL 业务任务记录"
        description="RabbitMQ 消息本身只携带任务 ID。这里展示对应的歌曲、原始文件名和处理元数据；隔离存储内部路径和文件哈希不会返回。"
      />

      <el-table
        v-loading="tasksLoading"
        :data="taskList ? [...taskList.items] : []"
        row-key="id"
        empty-text="当前没有等待或执行中的任务"
      >
        <el-table-column type="expand" width="46">
          <template #default="{ row }">
            <div class="task-details">
              <div><strong>任务 ID：</strong>{{ row.id }}</div>
              <div v-if="row.nextRetryAtUtc"><strong>下次重试：</strong>{{ formatTaskDate(row.nextRetryAtUtc) }}</div>
              <div v-if="row.providerTrackId"><strong>音源曲目 ID：</strong>{{ row.providerTrackId }}</div>
              <div v-if="row.sourcePageUrl"><strong>来源：</strong><a :href="row.sourcePageUrl" target="_blank" rel="noreferrer">{{ row.sourcePageUrl }}</a></div>
              <div v-if="row.errorCode || row.errorMessage" class="task-error">
                <strong>错误：</strong>{{ row.errorCode }} {{ row.errorMessage }}
              </div>
              <div><strong>原始任务数据：</strong></div>
              <pre>{{ row.detailsJson }}</pre>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="歌曲 / 文件" :sort-by="taskTitle" sortable min-width="220">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ taskTitle(row) }}</strong>
              <span v-if="row.originalFileName && row.title" class="text-faint">{{ row.title }}</span>
              <span v-if="row.albumTitle" class="text-faint">{{ row.albumTitle }}</span>
              <span v-if="row.artistsJson" class="text-faint">{{ row.artistsJson }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="阶段" prop="state" sortable width="95">
          <template #default="{ row }">
            <el-tag size="small" :type="row.state === 'processing' ? 'warning' : 'info'">
              {{ taskStateText(row.state) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="当前状态" prop="status" sortable min-width="135">
          <template #default="{ row }">{{ taskStatusText(row.status) }}</template>
        </el-table-column>
        <el-table-column label="音源 / 格式" :sort-by="taskProviderFormat" sortable min-width="130">
          <template #default="{ row }">
            {{ row.provider || [row.container, row.codec].filter(Boolean).join(' / ') || '—' }}
          </template>
        </el-table-column>
        <el-table-column label="大小" prop="fileSizeBytes" sortable width="90" align="right">
          <template #default="{ row }">{{ formatFileSize(row.fileSizeBytes) }}</template>
        </el-table-column>
        <el-table-column label="重试" prop="retryCount" sortable width="70" align="right">
          <template #default="{ row }">{{ row.retryCount }}</template>
        </el-table-column>
        <el-table-column label="创建时间" prop="createdAtUtc" sortable min-width="165">
          <template #default="{ row }">{{ formatTaskDate(row.createdAtUtc) }}</template>
        </el-table-column>
      </el-table>
      <el-pagination
        v-if="taskList && taskList.total > taskList.limit"
        class="task-pagination"
        background
        layout="prev, pager, next, jumper, total"
        :current-page="taskPage"
        :page-size="taskList.limit"
        :total="taskList.total"
        @current-change="changeTaskPage"
      />
    </div>
  </div>
</template>

<style scoped>
.mq-page {
  padding: 20px;
  max-width: 1300px;
}

.mode-tip {
  margin-bottom: 12px;
}

.task-panel {
  margin-top: 16px;
}

.task-pagination {
  justify-content: flex-end;
  margin-top: 16px;
}

.summary {
  font-size: 13px;
  margin-left: 12px;
}

.actions {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 12px;
}

.task-filters {
  gap: 8px;
}

.queue-name {
  font-family: var(--el-font-family-mono, monospace);
}

.task-details {
  display: grid;
  gap: 8px;
  padding: 8px 18px;
  overflow-wrap: anywhere;
}

.task-details pre {
  max-height: 320px;
  overflow: auto;
  margin: 0;
  padding: 12px;
  border-radius: 6px;
  background: var(--el-fill-color-light);
  white-space: pre-wrap;
}

.task-details a {
  color: var(--el-color-primary);
}

.task-error {
  color: var(--el-color-danger);
}

.text-danger {
  color: var(--el-color-danger);
}
</style>
