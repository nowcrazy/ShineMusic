<script setup lang="ts">
/**
 * 未匹配 / 无音源曲目明细弹窗（只读）。
 *
 * 合并两条不会创建下载任务的链路：
 * - queue：在歌单解析页勾选「加入下载队列」但没匹配到音源的曲目；
 * - playlist：导入歌单后后台补齐未匹配的曲目（含歌单名）。
 * 状态区分「no_source = 匹配不到直接结束」与「failed = 建了任务但全部音源失败」。
 */
import { computed, ref, watch } from 'vue'
import { Refresh } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { getUnmatchedTracks } from '@/services/admin-source-download'
import { describeError } from '@/services/errors'
import { formatDateTime } from '@/utils/format'
import type { ApiUnmatchedTrack } from '@/types/api.generated'

const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ (event: 'update:modelValue', value: boolean): void }>()

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value),
})

const loading = ref(false)
const status = ref('')
const keyword = ref('')
const rows = ref<readonly ApiUnmatchedTrack[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(100)
const noSource = ref(0)
const taskFailed = ref(0)

const statusOptions = computed(() => [
  { value: '', label: `全部（${noSource.value + taskFailed.value}）` },
  { value: 'no_source', label: `无音源（${noSource.value}）` },
  { value: 'failed', label: `下载失败（${taskFailed.value}）` },
])

const statusMeta: Record<string, { label: string; type: 'warning' | 'danger' }> = {
  no_source: { label: '无音源', type: 'warning' },
  failed: { label: '下载失败', type: 'danger' },
}

async function load() {
  loading.value = true
  try {
    const result = await getUnmatchedTracks({
      status: status.value || undefined,
      q: keyword.value.trim() || undefined,
      page: page.value,
      pageSize: pageSize.value,
    })
    rows.value = result.items
    total.value = result.total
    noSource.value = result.noSource
    taskFailed.value = result.taskFailed
  } catch (error) {
    ElMessage.error(describeError(error, '未匹配曲目加载失败'))
  } finally {
    loading.value = false
  }
}

/** 每次打开弹窗都回到第一页重新拉取，保证与最新入库状态一致。 */
watch(
  () => props.modelValue,
  (open) => {
    if (open) {
      page.value = 1
      void load()
    }
  },
)

function search() {
  page.value = 1
  void load()
}

function changeStatus() {
  page.value = 1
  void load()
}

function changePage(value: number) {
  page.value = value
  void load()
}

function changePageSize(size: number) {
  pageSize.value = size
  page.value = 1
  void load()
}

/** 来源列：独立加入下载队列没有歌单归属，导入歌单链路展示歌单名。 */
function originText(row: ApiUnmatchedTrack) {
  if (row.origin === 'queue') return '独立加入下载队列'
  return row.playlistName || '歌单导入'
}
</script>

<template>
  <el-dialog v-model="visible" title="未匹配 / 无音源曲目" width="1000px" top="6vh">
    <div class="unmatched-toolbar">
      <el-radio-group v-model="status" size="small" @change="changeStatus">
        <el-radio-button v-for="item in statusOptions" :key="item.value" :value="item.value">
          {{ item.label }}
        </el-radio-button>
      </el-radio-group>
      <div class="unmatched-search">
        <el-input
          v-model="keyword"
          size="small"
          placeholder="搜索曲名 / 艺人 / 专辑"
          clearable
          class="keyword-input"
          @keyup.enter="search"
          @clear="search"
        />
        <el-button size="small" type="primary" @click="search">查询</el-button>
        <el-button size="small" :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <el-table v-loading="loading" :data="rows" max-height="480" row-key="id" empty-text="暂无未匹配曲目">
      <el-table-column label="曲目" prop="title" min-width="180" show-overflow-tooltip>
        <template #default="{ row }">{{ row.title || '—' }}</template>
      </el-table-column>
      <el-table-column label="艺人" prop="artists" min-width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ row.artists.join(' / ') || '—' }}</template>
      </el-table-column>
      <el-table-column label="专辑" prop="albumTitle" min-width="140" show-overflow-tooltip>
        <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
      </el-table-column>
      <el-table-column label="来源平台" prop="sourcePlatform" min-width="100">
        <template #default="{ row }">{{ row.sourcePlatform || '—' }}</template>
      </el-table-column>
      <el-table-column label="来源" prop="origin" min-width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ originText(row) }}</template>
      </el-table-column>
      <el-table-column label="状态" prop="status" width="100">
        <template #default="{ row }">
          <el-tag size="small" :type="statusMeta[row.status]?.type || 'info'">
            {{ statusMeta[row.status]?.label || row.status }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column label="原因" prop="reason" min-width="320" show-overflow-tooltip>
        <template #default="{ row }">{{ row.reason || '—' }}</template>
      </el-table-column>
      <el-table-column label="创建时间" prop="createdAtUtc" width="170">
        <template #default="{ row }">{{ formatDateTime(row.createdAtUtc) }}</template>
      </el-table-column>
    </el-table>

    <div class="unmatched-pager">
      <el-pagination
        :current-page="page"
        :page-size="pageSize"
        :page-sizes="[100, 150, 200, 500, 1000]"
        :total="total"
        layout="total, sizes, prev, pager, next"
        background
        @current-change="changePage"
        @size-change="changePageSize"
      />
    </div>

    <template #footer>
      <span class="footer-hint">这些曲目未创建下载任务；需要人工处理请到「歌单导入」页重新补齐或换源。</span>
      <el-button @click="visible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.unmatched-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  flex-wrap: wrap;
}

.unmatched-search {
  display: flex;
  align-items: center;
  gap: 8px;
}

.keyword-input {
  width: 240px;
}

.unmatched-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
}

.footer-hint {
  margin-right: auto;
  color: var(--adm-text-3);
  font-size: 12px;
}
</style>
