<script setup lang="ts">
/**
 * 点歌需求管理：按状态/排序查看需求，推进状态、填写备注、关联曲目或重新开启。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import { getAdminSongRequests, reopenSongRequest, updateSongRequestStatus } from '@/services/admin-requests'
import { describeError } from '@/services/errors'
import { formatDate } from '@/utils/format'
import type { ApiSongRequest, SongRequestStatus } from '@/types/music'

const statusOptions: { value: SongRequestStatus | ''; label: string }[] = [
  { value: '', label: '全部状态' },
  { value: 'open', label: '待安排' },
  { value: 'planned', label: '已计划' },
  { value: 'in_progress', label: '处理中' },
  { value: 'fulfilled', label: '已入库' },
  { value: 'declined', label: '暂不收录' },
  { value: 'withdrawn', label: '已撤回' },
]

const statusText: Record<string, string> = {
  open: '待安排',
  planned: '已计划',
  in_progress: '处理中',
  fulfilled: '已入库',
  declined: '暂不收录',
  withdrawn: '已撤回',
}

const statusTone: Record<string, string> = {
  open: 'warning',
  planned: 'info',
  in_progress: 'primary',
  fulfilled: 'success',
  declined: 'danger',
  withdrawn: 'info',
}

const loading = ref(false)
const items = ref<ApiSongRequest[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const filter = reactive({ status: '' as SongRequestStatus | '', sort: 'votes' as 'votes' | 'createdAt' })

const dialogOpen = ref(false)
const current = ref<ApiSongRequest | null>(null)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({
  status: 'planned' as SongRequestStatus,
  adminNote: '',
  matchedTrackId: '',
})

async function load() {
  loading.value = true
  try {
    const result = await getAdminSongRequests({
      status: filter.status,
      sort: filter.sort,
      page: page.value,
      pageSize: pageSize.value,
    })
    items.value = [...result.items]
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '需求列表加载失败'))
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  void load()
}

function openUpdate(item: ApiSongRequest) {
  current.value = item
  form.status = item.status
  form.adminNote = ''
  form.matchedTrackId = item.matchedTrackId ?? ''
  dialogOpen.value = true
}

async function submitUpdate() {
  const item = current.value
  if (!item || saving.value) return
  if (form.status === 'declined' && !form.adminNote.trim()) {
    ElMessage.warning('拒绝需求时必须填写原因')
    return
  }
  if (form.status === 'fulfilled' && !form.matchedTrackId.trim()) {
    ElMessage.warning('标记已入库时需要填写匹配曲目 ID')
    return
  }
  saving.value = true
  try {
    const updated = await updateSongRequestStatus(item.id, {
      status: form.status,
      adminNote: form.adminNote.trim() || undefined,
      matchedTrackId: form.status === 'fulfilled' ? form.matchedTrackId.trim() : null,
    })
    const index = items.value.findIndex((row) => row.id === updated.id)
    if (index >= 0) items.value.splice(index, 1, updated)
    ElMessage.success('需求状态已更新')
    dialogOpen.value = false
  } catch (error) {
    ElMessage.error(describeError(error, '更新失败'))
  } finally {
    saving.value = false
  }
}

async function reopen(item: ApiSongRequest) {
  let reason = ''
  try {
    const result = await ElMessageBox.prompt('重新开启后提交人会收到通知，请填写原因。', '重新开启需求', {
      confirmButtonText: '开启',
      cancelButtonText: '取消',
      inputPlaceholder: '例如：已重新纳入排期',
      inputValidator: (value) => (value && value.trim() ? true : '原因不能为空'),
    })
    reason = result.value.trim()
  } catch {
    return
  }
  try {
    const updated = await reopenSongRequest(item.id, reason)
    const index = items.value.findIndex((row) => row.id === updated.id)
    if (index >= 0) items.value.splice(index, 1, updated)
    ElMessage.success('需求已重新开启')
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
        <h2>点歌需求</h2>
        <p>处理用户提交的求歌需求，推进状态并通知提交人</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
    </div>

    <div class="panel">
      <div class="filter-bar">
        <el-select v-model="filter.status" style="width: 150px" placeholder="全部状态" @change="search">
          <el-option v-for="option in statusOptions" :key="option.value" :label="option.label" :value="option.value" />
        </el-select>
        <el-select v-model="filter.sort" style="width: 150px" @change="search">
          <el-option label="投票优先" value="votes" />
          <el-option label="最新提交" value="createdAt" />
        </el-select>
        <el-button type="primary" :icon="Search" @click="search">查询</el-button>
        <span class="text-muted total">共 {{ total }} 条</span>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id" style="margin-top: 16px" empty-text="暂无需求">
        <el-table-column label="曲名" min-width="180">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title }}</strong>
              <span class="text-faint mono">{{ row.id }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="艺人" min-width="140" prop="artistName" />
        <el-table-column label="专辑" min-width="140">
          <template #default="{ row }">{{ row.albumTitle || '—' }}</template>
        </el-table-column>
        <el-table-column label="票数" width="80" prop="voteCount" />
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <el-tag size="small" :type="(statusTone[row.status] as any) || 'info'">
              {{ statusText[row.status] || row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="提交时间" width="120">
          <template #default="{ row }">{{ formatDate(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="180" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openUpdate(row)">更新状态</el-button>
            <el-button
              v-if="row.status === 'declined' || row.status === 'withdrawn'"
              link
              type="warning"
              @click="reopen(row)"
            >
              重新开启
            </el-button>
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

    <el-dialog v-model="dialogOpen" title="更新需求状态" width="520px">
      <el-form v-if="current" label-position="top">
        <el-form-item label="需求">
          <div class="request-brief">
            <strong>{{ current.title }}</strong>
            <span class="text-muted"> - {{ current.artistName }} · 当前 {{ statusText[current.status] || current.status }}</span>
          </div>
        </el-form-item>
        <el-form-item label="新状态">
          <el-select v-model="form.status" style="width: 100%">
            <el-option
              v-for="option in statusOptions.filter((item) => item.value !== 'withdrawn')"
              :key="option.value"
              :label="option.label"
              :value="option.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.status === 'fulfilled'" label="匹配曲目 ID">
          <el-input v-model="form.matchedTrackId" placeholder="发布入库的曲目 ID" />
        </el-form-item>
        <el-form-item label="管理员备注">
          <el-input
            v-model="form.adminNote"
            type="textarea"
            :rows="4"
            :placeholder="form.status === 'declined' ? '拒绝原因（必填，会发送给提交人）' : '可选，会记录到需求时间线'"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogOpen = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submitUpdate">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.total {
  margin-left: auto;
  font-size: 13px;
}

.title-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.request-brief {
  padding: 10px 12px;
  border-radius: 8px;
  background: #f5f7fa;
}
</style>
