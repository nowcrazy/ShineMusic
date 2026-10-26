<script setup lang="ts">
/**
 * 导入审核：批量上传音频/歌词，跟踪处理状态，试听并修正元数据后发布或拒绝。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import {
  ElMessage,
  ElMessageBox,
  type UploadFile,
  type UploadRawFile,
  type UploadUserFile,
} from 'element-plus'
import { Delete, Refresh, Upload, UploadFilled, View } from '@element-plus/icons-vue'
import {
  dismissImportItem,
  enrichImportItem,
  getPreviewUrl,
  listImportItems,
  publishImportItem,
  rejectImportItem,
  updateImportItem,
  uploadImportFilesBatch,
} from '@/services/admin-import'
import { describeError } from '@/services/errors'
import { formatTime } from '@/utils/format'
import type { ApiLibraryImportItem } from '@/types/api.generated'

const statusTabs = [
  { name: '', label: '全部' },
  { name: 'pending_review', label: '待审核' },
  { name: 'pending_publication', label: '待转公开' },
  { name: 'published', label: '已发布' },
  { name: 'rejected', label: '已拒绝' },
  { name: 'processing_failed', label: '处理失败' },
]

const statusText: Record<string, string> = {
  uploaded: '已上传',
  validating: '校验中',
  parsing_metadata: '解析元数据',
  transcoding: '转码中',
  pending_review: '待审核',
  ready_to_publish: '可发布',
  pending_publication: '待转公开',
  published: '已发布',
  rejected: '已拒绝',
  processing_failed: '处理失败',
}

const statusTone: Record<string, string> = {
  pending_review: 'warning',
  ready_to_publish: 'success',
  pending_publication: 'warning',
  published: 'success',
  rejected: 'info',
  processing_failed: 'danger',
}

const activeStatus = ref('')
const loading = ref(false)
const items = ref<ApiLibraryImportItem[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)

const drawerOpen = ref(false)
const current = ref<ApiLibraryImportItem | null>(null)
const saving = ref(false)
const enriching = ref(false)
const publishing = ref(false)
const forceFulfill = ref(false)
const overrideReason = ref('')
const form = reactive({
  title: '',
  artists: '',
  albumTitle: '',
  trackNumber: '' as string | number,
  discNumber: '' as string | number,
  releaseYear: '' as string | number,
  genres: '',
  lyrics: '',
})

const uploadOpen = ref(false)
const uploading = ref(false)
const uploadFiles = ref<UploadUserFile[]>([])
const uploadRequestId = ref('')

const previewUrl = computed(() => (current.value ? getPreviewUrl(current.value.id) : ''))
const canReview = (item: ApiLibraryImportItem) =>
  ['pending_review', 'ready_to_publish', 'pending_publication'].includes(item.status)
const canReject = (item: ApiLibraryImportItem) => !['published', 'rejected'].includes(item.status)
const canDismiss = (item: ApiLibraryImportItem) =>
  ['published', 'rejected', 'processing_failed'].includes(item.status)

async function loadItems() {
  loading.value = true
  try {
    const result = await listImportItems(activeStatus.value || undefined, page.value, pageSize.value)
    items.value = result.items.map((row) => row.item)
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '导入列表加载失败'))
  } finally {
    loading.value = false
  }
}

function changeTab() {
  page.value = 1
  void loadItems()
}

function openReview(item: ApiLibraryImportItem) {
  current.value = item
  form.title = item.metadata.title ?? ''
  form.artists = item.metadata.artists.join('、')
  form.albumTitle = item.metadata.albumTitle ?? ''
  form.trackNumber = item.metadata.trackNumber ?? ''
  form.discNumber = item.metadata.discNumber ?? ''
  form.releaseYear = item.metadata.releaseYear ?? ''
  form.genres = item.metadata.genres.join('、')
  form.lyrics = item.metadata.lyrics ?? ''
  forceFulfill.value = false
  overrideReason.value = ''
  drawerOpen.value = true
}

function splitValues(value: string) {
  return [...new Set(value.split(/[、,，]/).map((part) => part.trim()).filter(Boolean))]
}

function optionalNumber(value: string | number) {
  const text = String(value).trim()
  if (!text) return null
  const parsed = Number(text)
  return Number.isInteger(parsed) && parsed >= 0 ? parsed : null
}

async function saveMetadata() {
  const item = current.value
  if (!item || saving.value) return
  saving.value = true
  try {
    const updated = await updateImportItem(item.id, {
      title: form.title.trim() || undefined,
      artists: splitValues(form.artists),
      albumTitle: form.albumTitle.trim() || null,
      trackNumber: optionalNumber(form.trackNumber),
      discNumber: optionalNumber(form.discNumber),
      releaseYear: optionalNumber(form.releaseYear),
      genres: splitValues(form.genres),
      lyrics: form.lyrics.trim() || null,
    })
    current.value = { ...updated }
    replaceRow(current.value)
    ElMessage.success('元数据已保存')
  } catch (error) {
    ElMessage.error(describeError(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function enrich() {
  const item = current.value
  if (!item || enriching.value) return
  enriching.value = true
  try {
    const result = await enrichImportItem(item.id)
    current.value = { ...result.item }
    replaceRow(current.value)
    ElMessage.success(result.filled.length ? `已补全：${result.filled.join('、')}` : '没有可补全的字段')
    fillForm(current.value)
  } catch (error) {
    ElMessage.error(describeError(error, '一键补全失败'))
  } finally {
    enriching.value = false
  }
}

async function publish() {
  const item = current.value
  if (!item || publishing.value) return
  publishing.value = true
  try {
    const result = await publishImportItem(item.id, {
      forceFulfill: forceFulfill.value || undefined,
      overrideReason: forceFulfill.value ? overrideReason.value.trim() : undefined,
    })
    ElMessage.success(`已发布入库（曲目 ${result.trackId}）`)
    drawerOpen.value = false
    await loadItems()
  } catch (error) {
    ElMessage.error(describeError(error, '发布失败'))
  } finally {
    publishing.value = false
  }
}

async function reject(item: ApiLibraryImportItem) {
  try {
    await ElMessageBox.confirm(`确定拒绝「${item.metadata.title || item.sourceFile.displayName}」吗？隔离区文件会被删除。`, '拒绝导入', {
      confirmButtonText: '拒绝',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await rejectImportItem(item.id)
    ElMessage.success('已拒绝')
    if (current.value?.id === item.id) drawerOpen.value = false
    await loadItems()
  } catch (error) {
    ElMessage.error(describeError(error, '操作失败'))
  }
}

async function dismiss(item: ApiLibraryImportItem) {
  try {
    await dismissImportItem(item.id)
    ElMessage.success('已从列表移除')
    await loadItems()
  } catch (error) {
    ElMessage.error(describeError(error, '移除失败'))
  }
}

function fillForm(item: ApiLibraryImportItem) {
  form.title = item.metadata.title ?? ''
  form.artists = item.metadata.artists.join('、')
  form.albumTitle = item.metadata.albumTitle ?? ''
  form.trackNumber = item.metadata.trackNumber ?? ''
  form.discNumber = item.metadata.discNumber ?? ''
  form.releaseYear = item.metadata.releaseYear ?? ''
  form.genres = item.metadata.genres.join('、')
  form.lyrics = item.metadata.lyrics ?? ''
}

function replaceRow(item: ApiLibraryImportItem) {
  const index = items.value.findIndex((row) => row.id === item.id)
  if (index >= 0) items.value.splice(index, 1, item)
}

function openUpload() {
  uploadFiles.value = []
  uploadRequestId.value = ''
  uploadOpen.value = true
}

function onUploadChange(_file: UploadFile, files: UploadUserFile[]) {
  uploadFiles.value = files
}

async function submitUpload() {
  const rawFiles = uploadFiles.value
    .map((file) => file.raw)
    .filter((file): file is UploadRawFile => Boolean(file))
  if (rawFiles.length === 0) {
    ElMessage.warning('请选择至少一个音频文件')
    return
  }
  uploading.value = true
  try {
    await uploadImportFilesBatch(rawFiles, uploadRequestId.value.trim() || undefined)
    ElMessage.success('上传成功，已进入处理队列')
    uploadOpen.value = false
    activeStatus.value = 'pending_review'
    page.value = 1
    await loadItems()
  } catch (error) {
    ElMessage.error(describeError(error, '上传失败'))
  } finally {
    uploading.value = false
  }
}

const matchAlert = computed(() => current.value?.songRequestMatch ?? null)

onMounted(loadItems)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>导入审核</h2>
        <p>上传音频后自动解析、转码，人工确认元数据后发布入库</p>
      </div>
      <div class="heading-actions">
        <el-button :icon="Refresh" :loading="loading" @click="loadItems">刷新</el-button>
        <el-button type="primary" :icon="Upload" @click="openUpload">上传音频</el-button>
      </div>
    </div>

    <div class="panel">
      <el-tabs v-model="activeStatus" @tab-change="changeTab">
        <el-tab-pane v-for="tab in statusTabs" :key="tab.name" :label="tab.label" :name="tab.name" />
      </el-tabs>

      <el-table v-loading="loading" :data="items" row-key="id" empty-text="当前状态暂无条目">
        <el-table-column label="文件 / 曲名" min-width="230">
          <template #default="{ row }">
            <div class="file-cell">
              <strong>{{ row.metadata.title || row.sourceFile.displayName }}</strong>
              <span class="text-faint">
                {{ row.sourceFile.displayName }} · {{ row.sourceFile.container || '未知格式' }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="艺人" min-width="140">
          <template #default="{ row }">{{ row.metadata.artists.join('、') || '—' }}</template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="(statusTone[row.status] as any) || 'info'">
              {{ statusText[row.status] || row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="转码" width="120">
          <template #default="{ row }">
            <div class="transcode">
              <span>{{ row.transcode.status || '—' }}</span>
              <span class="text-faint">{{ row.transcode.outputFormat || '' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="关联需求" width="100">
          <template #default="{ row }">
            <el-tag v-if="row.songRequestId" size="small" type="warning">已关联</el-tag>
            <span v-else class="text-faint">—</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="210" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canReview(row)" link type="primary" :icon="View" @click="openReview(row)">
              审核
            </el-button>
            <el-button v-if="canReject(row)" link type="danger" :icon="Delete" @click="reject(row)">
              拒绝
            </el-button>
            <el-button v-if="canDismiss(row)" link @click="dismiss(row)">移除</el-button>
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
          @current-change="loadItems"
          @size-change="changeTab"
        />
      </div>
    </div>

    <el-drawer v-model="drawerOpen" title="审核导入条目" size="620px" destroy-on-close>
      <template v-if="current">
        <el-alert
          v-if="matchAlert"
          class="match-alert"
          :type="matchAlert.kind === 'match' ? 'success' : matchAlert.blocking ? 'error' : 'warning'"
          :title="matchAlert.message"
          :closable="false"
          show-icon
        >
          <div class="match-detail">
            曲目：{{ matchAlert.trackDisplay }} ｜ 需求：{{ matchAlert.requestDisplay }}
          </div>
        </el-alert>

        <div class="source-card">
          <div class="source-row">
            <span class="text-muted">源文件</span>
            <span>{{ current.sourceFile.displayName }}（{{ current.sourceFile.container || '未知' }} / {{ current.sourceFile.codec || '未知编码' }}）</span>
          </div>
          <div class="source-row">
            <span class="text-muted">时长</span>
            <span>{{ formatTime((current.metadata.durationMs ?? 0) / 1000) }}</span>
          </div>
          <div class="source-row">
            <span class="text-muted">转码</span>
            <span>{{ current.transcode.status || '未开始' }} {{ current.transcode.outputFormat || '' }} {{ current.transcode.errorCode || '' }}</span>
          </div>
          <div v-if="current.warnings.length" class="warnings">
            <el-tag v-for="warning in current.warnings" :key="warning" size="small" type="warning">
              {{ warning }}
            </el-tag>
          </div>
          <audio v-if="previewUrl" class="preview" :src="previewUrl" controls preload="none" />
        </div>

        <el-form label-position="top" class="edit-form">
          <el-form-item label="曲名">
            <el-input v-model="form.title" placeholder="留空则使用源文件解析结果" />
          </el-form-item>
          <el-form-item label="艺人（顿号或逗号分隔）">
            <el-input v-model="form.artists" placeholder="例如：周杰伦、费玉清" />
          </el-form-item>
          <el-form-item label="专辑">
            <el-input v-model="form.albumTitle" />
          </el-form-item>
          <div class="form-row">
            <el-form-item label="曲序">
              <el-input v-model="form.trackNumber" type="number" />
            </el-form-item>
            <el-form-item label="碟序">
              <el-input v-model="form.discNumber" type="number" />
            </el-form-item>
            <el-form-item label="发行年">
              <el-input v-model="form.releaseYear" type="number" />
            </el-form-item>
          </div>
          <el-form-item label="风格（顿号或逗号分隔）">
            <el-input v-model="form.genres" />
          </el-form-item>
          <el-form-item label="歌词">
            <el-input v-model="form.lyrics" type="textarea" :rows="6" placeholder="可粘贴 LRC 或纯文本歌词" />
          </el-form-item>

          <el-form-item v-if="matchAlert && matchAlert.kind !== 'match'" label="强制关联需求">
            <div class="force-block">
              <el-switch v-model="forceFulfill" />
              <el-input
                v-if="forceFulfill"
                v-model="overrideReason"
                placeholder="填写强制关联原因（会记入审计）"
                style="max-width: 360px"
              />
            </div>
          </el-form-item>

          <div class="drawer-actions">
            <el-button :icon="Refresh" :loading="enriching" @click="enrich">一键补全</el-button>
            <el-button :loading="saving" @click="saveMetadata">保存元数据</el-button>
            <el-button type="danger" plain @click="reject(current)">拒绝</el-button>
            <el-button type="primary" :loading="publishing" @click="publish">发布入库</el-button>
          </div>
        </el-form>
      </template>
    </el-drawer>

    <el-dialog v-model="uploadOpen" title="上传音频" width="620px">
      <el-upload
        drag
        multiple
        :auto-upload="false"
        :on-change="onUploadChange"
        :on-remove="onUploadChange"
        accept=".mp3,.flac,.m4a,.aac,.wav,.ogg,.opus,.lrc,.txt"
        :file-list="uploadFiles"
      >
        <el-icon class="upload-icon"><UploadFilled /></el-icon>
        <div class="el-upload__text">拖拽文件到此处，或<em>点击选择</em></div>
        <template #tip>
          <div class="upload-tip">
            支持音频与同名歌词文件（.lrc / .txt）一同上传，后端会按文件名主干自动配对。
          </div>
        </template>
      </el-upload>
      <el-form label-position="top" style="margin-top: 16px">
        <el-form-item label="关联点歌需求 ID（可选）">
          <el-input v-model="uploadRequestId" placeholder="填写后发布时会自动满足该需求" clearable />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="uploadOpen = false">取消</el-button>
        <el-button type="primary" :loading="uploading" @click="submitUpload">开始上传</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  gap: 10px;
}

.file-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.file-cell strong,
.file-cell span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.transcode {
  display: flex;
  flex-direction: column;
  font-size: 12px;
}

.match-alert {
  margin-bottom: 16px;
}

.match-detail {
  margin-top: 4px;
  font-size: 12px;
}

.source-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 14px;
  margin-bottom: 20px;
  border: 1px solid var(--adm-border);
  border-radius: 8px;
  background: #fafbfc;
  font-size: 13px;
}

.source-row {
  display: grid;
  grid-template-columns: 64px 1fr;
  gap: 8px;
}

.warnings {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.preview {
  width: 100%;
  margin-top: 4px;
}

.form-row {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.force-block {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.drawer-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  margin-top: 8px;
}

.upload-icon {
  margin-bottom: 8px;
  color: var(--adm-text-3);
  font-size: 40px;
}

.upload-tip {
  margin-top: 8px;
  color: var(--adm-text-3);
  font-size: 12px;
  line-height: 1.45;
}
</style>
