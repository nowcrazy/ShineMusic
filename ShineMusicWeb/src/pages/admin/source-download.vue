<script setup lang="ts">
/**
 * 音源抓歌：从 Audius / Jamendo 等外部平台搜索曲目，批量创建下载任务并跟踪结果。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import { Download, Refresh, Search } from '@element-plus/icons-vue'
import {
  createDownloadTasks,
  getDownloadTasks,
  retryDownloadTask,
  searchSources,
  switchSource,
  SourceProviderLabel,
  type ExternalDownloadTask,
  type SourceProviderFilter,
  type SourceSearchMode,
  type SourceSearchItem,
} from '@/services/admin-source-download'
import { describeError } from '@/services/errors'
import { formatTime } from '@/utils/format'
import type { ApiSourceProviderError } from '@/types/api.generated'

const providerOptions: { value: SourceProviderFilter; label: string }[] = [
  { value: 'all', label: '全部平台' },
  { value: 'netease', label: 'sourceDownload.providerNetease' },
  { value: 'kuwo', label: 'sourceDownload.providerKuwo' },
  { value: 'kugou', label: 'sourceDownload.providerKugou' },
  { value: 'migu', label: 'sourceDownload.providerMigu' },
  { value: 'audius', label: 'Audius' },
  { value: 'jamendo', label: 'Jamendo' },
]

const { t } = useI18n()
const searchModeOptions: { value: SourceSearchMode; label: string }[] = [
  { value: 'title', label: 'sourceDownload.searchModeTitle' },
  { value: 'artist', label: 'sourceDownload.searchModeArtist' },
  { value: 'artist-track', label: 'sourceDownload.searchModeArtistTrack' },
  { value: 'artist-album', label: 'sourceDownload.searchModeArtistAlbum' },
  { value: 'album', label: 'sourceDownload.searchModeAlbum' },
]
const searchPlaceholderKeys: Record<SourceSearchMode, string> = {
  title: 'sourceDownload.titleSearchPlaceholder',
  artist: 'sourceDownload.artistSearchPlaceholder',
  'artist-track': 'sourceDownload.artistTrackSearchPlaceholder',
  'artist-album': 'sourceDownload.artistAlbumSearchPlaceholder',
  album: 'sourceDownload.albumSearchPlaceholder',
}

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
const searchForm = reactive({
  query: '',
  provider: 'all' as SourceProviderFilter,
  mode: 'title' as SourceSearchMode,
  limit: 20,
})
const searching = ref(false)
const results = ref<SourceSearchItem[]>([])
const providerErrors = ref<ApiSourceProviderError[]>([])
const selected = ref<SourceSearchItem[]>([])
const creating = ref(false)
const resultSort = ref('quality:asc')
const resultSortOptions = [
  { value: 'quality:asc', label: 'sourceDownload.sortQuality' },
  { value: 'releaseYear:desc', label: 'sourceDownload.sortYearNewest' },
  { value: 'releaseYear:asc', label: 'sourceDownload.sortYearOldest' },
  { value: 'title:asc', label: 'sourceDownload.sortTitleAsc' },
  { value: 'title:desc', label: 'sourceDownload.sortTitleDesc' },
  { value: 'durationMs:desc', label: 'sourceDownload.sortDuration' },
]
const sortedResults = computed(() => {
  const [field, direction] = resultSort.value.split(':')
  const sign = direction === 'desc' ? -1 : 1
  return [...results.value].sort((a, b) => {
    if (field === 'quality') {
      if (searchForm.mode === 'album' || searchForm.mode === 'artist-album') {
        const groupOrder = `${a.albumTitle ?? ''}|${a.artists.join(',')}|${a.provider}`
          .localeCompare(`${b.albumTitle ?? ''}|${b.artists.join(',')}|${b.provider}`)
        if (groupOrder !== 0) return groupOrder
      }
      return sign * ((qualityRank[a.quality] ?? 3) - (qualityRank[b.quality] ?? 3))
    }
    if (field === 'releaseYear') {
      if (a.releaseYear == null) return b.releaseYear == null ? 0 : 1
      if (b.releaseYear == null) return -1
      return sign * (a.releaseYear - b.releaseYear)
    }
    if (field === 'durationMs') return sign * ((a.durationMs ?? 0) - (b.durationMs ?? 0))
    const left = field === 'title' ? a.title : a.artists.join(' ')
    const right = field === 'title' ? b.title : b.artists.join(' ')
    return sign * left.localeCompare(right)
  })
})

// ===== 任务 =====
const taskLoading = ref(false)
const tasks = ref<ExternalDownloadTask[]>([])
const taskTotal = ref(0)
const taskPage = ref(1)
const taskPageSize = ref(100)
const taskStatus = ref('')
const taskSort = ref('createdAt:desc')
const taskSortOptions = [
  { value: 'createdAt:desc', label: 'sourceDownload.sortNewest' },
  { value: 'createdAt:asc', label: 'sourceDownload.sortOldest' },
  { value: 'releaseYear:desc', label: 'sourceDownload.sortYearNewest' },
  { value: 'releaseYear:asc', label: 'sourceDownload.sortYearOldest' },
  { value: 'title:asc', label: 'sourceDownload.sortTitleAsc' },
  { value: 'title:desc', label: 'sourceDownload.sortTitleDesc' },
  { value: 'status:asc', label: 'sourceDownload.sortStatus' },
]
/** 任务关键词：服务端匹配曲名 / 艺人 / 专辑。 */
const taskKeyword = ref('')

// 音质排序权重（与后端 SourceQualities.Rank 对齐，值越小越好）。
const qualityRank: Record<string, number> = {
  lossless: 0,
  high: 1,
  standard: 2,
}

function onSelectionChange(rows: SourceSearchItem[]) {
  selected.value = rows
}

function resultKey(item: SourceSearchItem): string {
  return `${item.provider}:${item.providerTrackId}`
}

function providerLabel(provider: string): string {
  const label = SourceProviderLabel[provider] || provider
  return label.startsWith('sourceDownload.') ? t(label) : label
}

async function runSearch() {
  if (!searchForm.query.trim()) {
    ElMessage.warning('请输入搜索关键词')
    return
  }
  searching.value = true
  try {
    const result = await searchSources(
      searchForm.query.trim(),
      searchForm.provider,
      searchForm.limit,
      searchForm.mode,
    )
    // 按音质从好到坏排序：lossless → high → standard → unknown。
    results.value = [...result.results]
    providerErrors.value = [...result.providerErrors]
    selected.value = []
    if (results.value.length === 0) ElMessage.info(t('sourceDownload.noExactResults'))
    else if (result.filteredCount && result.filteredCount > 0)
      ElMessage.info(`已过滤 ${result.filteredCount} 首不可下载的曲目（VIP/无版权/占位资源）`)
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
    const [sortBy, sortDirection] = taskSort.value.split(':')
    const result = await getDownloadTasks(
      taskStatus.value || undefined,
      taskPage.value,
      taskPageSize.value,
      taskKeyword.value.trim() || undefined,
      sortBy,
      sortDirection,
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

function handleTaskSortChange({
  prop,
  order,
}: {
  prop: string | null
  order: 'ascending' | 'descending' | null
}) {
  const fieldMap: Record<string, string> = {
    title: 'title',
    releaseYear: 'releaseYear',
    status: 'status',
  }
  if (!order || !prop || !fieldMap[prop]) return
  taskSort.value = `${fieldMap[prop]}:${order === 'ascending' ? 'asc' : 'desc'}`
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

async function switchSourceForTask(task: ExternalDownloadTask) {
  try {
    const result = await switchSource(task.id)
    if (result.autoMatched) {
      ElMessage.success(`已换源到 ${providerLabel(result.newProvider)}，新任务已创建`)
    } else {
      ElMessage.warning(result.message || `换源到 ${providerLabel(result.newProvider)}，请确认后下载`)
    }
    await loadTasks()
  } catch (error) {
    ElMessage.error(describeError(error, '换源失败'))
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
        <el-select v-model="searchForm.mode" style="width: 110px">
          <el-option
            v-for="option in searchModeOptions"
            :key="option.value"
            :label="t(option.label)"
            :value="option.value"
          />
        </el-select>
        <el-input
          v-model="searchForm.query"
          :placeholder="t(searchPlaceholderKeys[searchForm.mode])"
          style="width: 280px"
          clearable
          @keyup.enter="runSearch"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-select v-model="searchForm.provider" style="width: 150px">
          <el-option
            v-for="option in providerOptions"
            :key="option.value"
            :label="option.label.startsWith('sourceDownload.') ? t(option.label) : option.label"
            :value="option.value"
          />
        </el-select>
        <el-select v-model="searchForm.limit" style="width: 110px">
          <el-option v-for="count in [10, 20, 30, 50]" :key="count" :label="`${count} 条`" :value="count" />
        </el-select>
        <el-select v-model="resultSort" style="width: 150px" :placeholder="t('sourceDownload.sortLabel')">
          <el-option
            v-for="option in resultSortOptions"
            :key="option.value"
            :label="t(option.label)"
            :value="option.value"
          />
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
          {{ providerLabel(error.provider) }}：{{ error.message }}
        </div>
      </el-alert>

      <el-table
        v-loading="searching"
        :data="sortedResults"
        :row-key="resultKey"
        style="margin-top: 14px"
        empty-text="输入关键词搜索外部音源"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="46" />
        <el-table-column label="曲名 / 艺人" prop="title" sortable min-width="240">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title }}</strong>
              <span class="text-faint">{{ row.artists.join('、') || '未知艺人' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="平台" prop="provider" sortable width="110">
          <template #default="{ row }">
            <el-tag size="small" type="info">{{ providerLabel(row.provider) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="专辑" prop="albumTitle" sortable min-width="150">
          <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
        </el-table-column>
        <el-table-column :label="t('sourceDownload.releaseYearLabel')" prop="releaseYear" sortable width="100">
          <template #default="{ row }">{{ row.releaseYear ?? '—' }}</template>
        </el-table-column>
        <el-table-column label="时长" prop="durationMs" sortable width="80">
          <template #default="{ row }">{{ formatTime((row.durationMs ?? 0) / 1000) }}</template>
        </el-table-column>
        <el-table-column label="音质" prop="quality" sortable width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isLosslessAvailable ? 'success' : 'info'">
              {{ qualityText[row.quality] || row.quality }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="来源页" prop="sourcePageUrl" sortable width="90">
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
        <el-select v-model="taskSort" style="width: 170px" size="small" @change="changeTaskFilter">
          <el-option
            v-for="option in taskSortOptions"
            :key="option.value"
            :label="t(option.label)"
            :value="option.value"
          />
        </el-select>
      </div>

      <el-table
        v-loading="taskLoading"
        :data="tasks"
        row-key="id"
        empty-text="暂无下载任务"
        @sort-change="handleTaskSortChange"
      >
        <el-table-column label="曲名 / 艺人" prop="title" sortable="custom" min-width="230">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title || row.providerTrackId }}</strong>
              <span class="text-faint">{{ row.artists.join('、') || '—' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="平台" prop="provider" sortable width="110">
          <template #default="{ row }">
            <el-tag size="small" type="info">{{ providerLabel(row.provider) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="t('sourceDownload.releaseYearLabel')" prop="releaseYear" sortable="custom" width="100">
          <template #default="{ row }">{{ row.releaseYear ?? '—' }}</template>
        </el-table-column>
        <el-table-column label="状态" prop="status" sortable="custom" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="(taskStatusTone[row.status] as any) || 'info'">
              {{ taskStatusText[row.status] || row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="音质" prop="quality" sortable width="90">
          <template #default="{ row }">{{ row.quality ? qualityText[row.quality] || row.quality : '—' }}</template>
        </el-table-column>
        <el-table-column label="自动发布" prop="autoPublished" sortable width="90">
          <template #default="{ row }">
            <el-tag v-if="row.autoPublished" size="small" type="success">是</el-tag>
            <span v-else class="text-faint">否</span>
          </template>
        </el-table-column>
        <el-table-column label="失败原因" prop="errorMessage" sortable min-width="180">
          <template #default="{ row }">
            <span v-if="row.errorMessage" class="text-muted">{{ row.errorCode || '' }} {{ row.errorMessage }}</span>
            <span v-else class="text-faint">—</span>
          </template>
        </el-table-column>
        <el-table-column label="重试" prop="autoRetryCount" sortable width="70">
          <template #default="{ row }">{{ row.autoRetryCount }}</template>
        </el-table-column>
        <el-table-column label="操作" width="130" fixed="right">
          <template #default="{ row }">
            <template v-if="row.status === 'failed'">
              <el-button link type="primary" @click="retry(row)">重试</el-button>
              <el-button link type="warning" @click="switchSourceForTask(row)">换源</el-button>
            </template>
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
