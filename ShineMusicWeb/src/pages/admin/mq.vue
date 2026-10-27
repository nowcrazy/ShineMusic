<script setup lang="ts">
/**
 * 消息队列监控：查看 RabbitMQ 各队列的积压、处理中、在线消费者与收发速率，支持 10s 自动刷新。
 * 数据来自后端转发的 RabbitMQ Management HTTP API；插件不可达时提示 502 原因。
 */
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import { getMessageQueues } from '@/services/admin-mq'
import { describeError } from '@/services/errors'
import type { ApiMessageQueueStatus } from '@/types/api.generated'

const loading = ref(false)
const queues = ref<ApiMessageQueueStatus[]>([])
const loadError = ref('')
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
        <el-table-column label="队列名" min-width="230">
          <template #default="{ row }">
            <div class="title-cell">
              <strong class="queue-name">{{ row.name }}</strong>
              <span class="text-faint">{{ row.role }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="stateTagType(row.state)">{{ stateText(row.state) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="排队中" width="90" align="right">
          <template #default="{ row }">
            <strong :class="{ 'text-danger': row.messagesReady > 0 }">{{ row.messagesReady }}</strong>
          </template>
        </el-table-column>
        <el-table-column label="处理中" width="90" align="right">
          <template #default="{ row }">{{ row.messagesUnacknowledged }}</template>
        </el-table-column>
        <el-table-column label="消费者" width="90" align="right">
          <template #default="{ row }">
            <strong :class="{ 'text-danger': row.consumers === 0 }">{{ row.consumers }}</strong>
          </template>
        </el-table-column>
        <el-table-column label="预取" width="80" align="right">
          <template #default="{ row }">{{ row.prefetch }}</template>
        </el-table-column>
        <el-table-column label="消费模式" width="120" align="center">
          <template #default="{ row }">
            <el-tag size="small" :type="modeTagType(row)" effect="plain">{{ modeText(row) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="并行度" width="90" align="right">
          <template #default="{ row }">{{ row.concurrency }}</template>
        </el-table-column>
        <el-table-column label="发布速率" width="100" align="right">
          <template #default="{ row }">{{ formatRate(row.publishRate) }}</template>
        </el-table-column>
        <el-table-column label="投递速率" width="100" align="right">
          <template #default="{ row }">{{ formatRate(row.deliverRate) }}</template>
        </el-table-column>
      </el-table>
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

.queue-name {
  font-family: var(--el-font-family-mono, monospace);
}

.text-danger {
  color: var(--el-color-danger);
}
</style>
