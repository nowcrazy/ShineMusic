<script setup lang="ts">
/**
 * 音源抓歌：从 Audius / Jamendo 等外部平台搜索曲目，批量创建下载任务并跟踪结果。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, Refresh, Search } from '@element-plus/icons-vue'
import {
  createDownloadTasks,
  getDownloadTasks,
  retryDownloadTask,
  searchSources,
  SourceProviderLabel,
  type ExternalDownloadTask,
  type SourceProviderFilter,
  type SourceSearchItem,
} from '@/services/admin-source-download'
import { describeError } from '@/services/errors'
import { formatTime } from '@/utils/format'
import type { ApiSourceProviderError } from '@/types/api.generated'

const providerOptions: { value: SourceProviderFilter; label: string }[] = [
  { value: 'all', label: '全部平台' },
  { value: 'netease', label: '网易云' },
  { value: 'kuwo', label: '酷我' },
  { value: 'kugou', label: '酷狗' },
  { value: 'audius', label: 'Audius' },
  { value: 'jamendo', label: 'Jamendo' },
  { value: 'ytmusic', label: 'YT Music' },
  { value: 'soundcloud', label: 'SoundCloud' },
]

const taskStatusOptions = [
  { value: '', label: '全部状态' },
  { value: 'queued', label: '排队中' },
  { value: 'downloading', label: '下载中' },
  { value: 'import_queued', label: '入库处理中' },
  { value: 'completed', label: '已完成' },
  { value: 'failed', label: '失败' },
]

const taskStatusText: Record<string, string> = {
  queued: '排队中',
  downloading: '下载中',
  import_queued: '入库处理中',
  completed: '已完成',
  failed: '失败',
}

const taskStatusTone: Record<string, string> = {
  queued: 'info',
  downloading: 'primary',
  import_queued: 'warning',
  completed: 'success',
  failed: 'danger',
}

const qualityText: Record<string, string> = {
  lossless: '无损',
  high: '高品质',
  standard: '标准',
}

// ===== 搜索 =====
const searchForm = reactive({ query: '', provider: 'all' as SourceProviderFilter, limit: 20 })
const searching = ref(false)
const results = ref<SourceSearchItem[]>([])
const providerErrors = ref<ApiSourceProviderError[]>([])
const selected = ref<SourceSearchItem[]>([])
const creating = ref(false)

// ===== 任务 =====
const taskLoading = ref(false)
const tasks = ref<ExternalDownloadTask[]>([])
const taskTotal = ref(0)
const taskPage = ref(1)
const taskPageSize = ref(100)
const taskStatus = ref('')
/** 任务关键词：服务端匹配曲名 / 艺人 / 专辑。 */
const taskKeyword = ref('')

const selectedProvider = () => selected.value[0]?.provider ?? null

function selectable(row: SourceSearchItem) {
  const provider = selectedProvider()
  return !provider || provider === row.provider
}

function onSelectionChange(rows: SourceSearchItem[]) {
  selected.value = rows
}

async function runSearch() {
  if (!searchForm.query.trim()) {
    ElMessage.warning('请输入搜索关键词')
    return
  }
  searching.value = true
  try {
    const result = await searchSources(searchForm.query.trim(), searchForm.provider, searchForm.limit)
    results.value = [...result.results]
    providerErrors.value = [...result.providerErrors]
    selected.value = []
    if (results.value.length === 0) ElMessage.info('没有找到匹配的音源')
  } catch (error) {
    ElMessage.error(describeError(error, '音源搜索失败'))
  } finally {
    searching.value = false
  }
}

async function downloadSelected() {
  if (selected.value.length === 0) {
    ElMessage.warning('请先选择要下载的曲目')
    return
  }
  creating.value = true
  try {
    const result = await createDownloadTasks(selected.value)
    ElMessage.success(
      `已创建 ${result.created.length} 个下载任务` +
        (result.skippedDuplicates.length ? `，跳过 ${result.skippedDuplicates.length} 首重复曲目` : ''),
    )
    selected.value = []
    await loadTasks()
  } catch (error) {
    ElMessage.error(describeError(error, '创建下载任务失败'))
  } finally {
    creating.value = false
  }
}

async function loadTasks() {
  taskLoading.value = true
  try {
    const result = await getDownloadTasks(
      taskStatus.value || undefined,
      taskPage.value,
      taskPageSize.value,
      taskKeyword.value.trim() || undefined,
    )
    tasks.value = [...result.items]
    taskTotal.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '任务列表加载失败'))
  } finally {
    taskLoading.value = false
  }
}

function changeTaskFilter() {
  taskPage.value = 1
  void loadTasks()
}

async function retry(task: ExternalDownloadTask) {
  try {
    await retryDownloadTask(task.id)
    ElMessage.success('已重新排队')
    await loadTasks()
  } catch (error) {
    ElMessage.error(describeError(error, '重试失败'))
  }
}

onMounted(loadTasks)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>音源抓歌</h2>
        <p>从外部平台搜索曲目，派发下载任务，自动入库或转人工审核</p>
      </div>
      <el-button :icon="Refresh" :loading="taskLoading" @click="loadTasks">刷新任务</el-button>
    </div>

    <div class="panel">
      <div class="panel-title">搜索音源</div>
      <div class="filter-bar">
        <el-input
          v-model="searchForm.query"
          placeholder="曲名 / 艺人"
          style="width: 280px"
          clearable
          @keyup.enter="runSearch"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-select v-model="searchForm.provider" style="width: 150px">
          <el-option v-for="option in providerOptions" :key="option.value" :label="option.label" :value="option.value" />
        </el-select>
        <el-select v-model="searchForm.limit" style="width: 110px">
          <el-option v-for="count in [10, 20, 30, 50]" :key="count" :label="`${count} 条`" :value="count" />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="searching" @click="runSearch">搜索</el-button>
        <el-button
          type="success"
          :icon="Download"
          :loading="creating"
          :disabled="selected.length === 0"
          @click="downloadSelected"
        >
          下载选中（{{ selected.length }}）
        </el-button>
      </div>

      <el-alert
        v-if="providerErrors.length"
        class="provider-errors"
        type="warning"
        :closable="false"
        show-icon
        title="部分平台搜索失败"
      >
        <div v-for="error in providerErrors" :key="error.provider">
          {{ SourceProviderLabel[error.provider] || error.provider }}：{{ error.message }}
        </div>
      </el-alert>

      <el-table
        v-loading="searching"
        :data="results"
        row-key="providerTrackId"
        style="margin-top: 14px"
        empty-text="输入关键词搜索外部音源"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="46" :selectable="selectable" />
        <el-table-column label="曲名 / 艺人" min-width="240">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title }}</strong>
              <span class="text-faint">{{ row.artists.join('、') || '未知艺人' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="平台" width="110">
          <template #default="{ row }">
            <el-tag size="small" type="info">{{ SourceProviderLabel[row.provider] || row.provider }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="专辑" min-width="150">
          <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
        </el-table-column>
        <el-table-column label="时长" width="80">
          <template #default="{ row }">{{ formatTime((row.durationMs ?? 0) / 1000) }}</template>
        </el-table-column>
        <el-table-column label="音质" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isLosslessAvailable ? 'success' : 'info'">
              {{ qualityText[row.quality] || row.quality }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="来源页" width="90">
          <template #default="{ row }">
            <a v-if="row.sourcePageUrl" :href="row.sourcePageUrl" target="_blank" rel="noreferrer">打开</a>
            <span v-else class="text-faint">—</span>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div class="panel">
      <div class="panel-title">
        下载任务
        <el-input
          v-model="taskKeyword"
          placeholder="搜索曲名 / 艺人 / 专辑"
          size="small"
          clearable
          class="task-keyword"
          @keyup.enter="changeTaskFilter"
          @clear="changeTaskFilter"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-select v-model="taskStatus" style="width: 150px" size="small" placeholder="全部状态" @change="changeTaskFilter">
          <el-option v-for="option in taskStatusOptions" :key="option.value" :label="option.label" :value="option.value" />
        </el-select>
      </div>

      <el-table v-loading="taskLoading" :data="tasks" row-key="id" empty-text="暂无下载任务">
        <el-table-column label="曲名 / 艺人" min-width="230">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title || row.providerTrackId }}</strong>
              <span class="text-faint">{{ row.artists.join('、') || '—' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="平台" width="110">
          <template #default="{ row }">
            <el-tag size="small" type="info">{{ SourceProviderLabel[row.provider] || row.provider }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="(taskStatusTone[row.status] as any) || 'info'">
              {{ taskStatusText[row.status] || row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="音质" width="90">
          <template #default="{ row }">{{ row.quality ? qualityText[row.quality] || row.quality : '—' }}</template>
        </el-table-column>
        <el-table-column label="自动发布" width="90">
          <template #default="{ row }">
            <el-tag v-if="row.autoPublished" size="small" type="success">是</el-tag>
            <span v-else class="text-faint">否</span>
          </template>
        </el-table-column>
        <el-table-column label="失败原因" min-width="180">
          <template #default="{ row }">
            <span v-if="row.errorMessage" class="text-muted">{{ row.errorCode || '' }} {{ row.errorMessage }}</span>
            <span v-else class="text-faint">—</span>
          </template>
        </el-table-column>
        <el-table-column label="重试" width="70">
          <template #default="{ row }">{{ row.autoRetryCount }}</template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button v-if="row.status === 'failed'" link type="primary" @click="retry(row)">重试</el-button>
            <span v-else class="text-faint">—</span>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination">
        <el-pagination
          v-model:current-page="taskPage"
          v-model:page-size="taskPageSize"
          :total="taskTotal"
          :page-sizes="[100, 150, 200, 500, 1000]"
          layout="total, sizes, prev, pager, next"
          @current-change="loadTasks"
          @size-change="changeTaskFilter"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.provider-errors {
  margin-top: 14px;
  font-size: 12px;
}

.task-keyword {
  width: 240px;
  margin-left: 12px;
}

.title-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.title-cell strong,
.title-cell span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
