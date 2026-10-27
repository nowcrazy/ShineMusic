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
import { Delete, InfoFilled, Refresh, Search, Upload, UploadFilled, View } from '@element-plus/icons-vue'
import {
  dismissImportItem,
  enrichImportItem,
  getPreviewUrl,
  listImportItems,
  publishImportItem,
  rejectImportItem,
  updateImportItem,
  uploadImportFilesBatch,
  bulkPublishCandidates,
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
/** 关键词：服务端匹配曲名 / 艺人 / 专辑 / 源文件名。 */
const keyword = ref('')
const loading = ref(false)
const items = ref<ApiLibraryImportItem[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(100)
/** 服务端排序：默认按创建时间倒序；评分/标题/状态/时长可按列头切换。 */
const sortField = ref('created')
const sortOrder = ref('desc')

const drawerOpen = ref(false)
const current = ref<ApiLibraryImportItem | null>(null)
const saving = ref(false)
const enriching = ref(false)
const publishing = ref(false)
const bulkPublishing = ref(false)
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

/** AutoScore 分段颜色：≥85 绿（优质）、75-84 黄（达自动发布线）、<75 红（需人工）、null 灰（未评分）。 */
function scoreTone(score: number | null | undefined) {
  if (score == null) return { color: 'var(--adm-text-3)', tone: 'info' as const, label: '未评' }
  if (score >= 85) return { color: '#22c55e', tone: 'success' as const, label: '优' }
  if (score >= 75) return { color: '#f59e0b', tone: 'warning' as const, label: '良' }
  return { color: '#ef4444', tone: 'danger' as const, label: '低分' }
}

/** 待审核条目里高分可自动发布的统计提示。 */
const pendingScoreStats = computed(() => {
  const pending = items.value.filter(i => i.status === 'pending_review')
  const autoPublish = pending.filter(i => (i.autoScore ?? 0) >= 75).length
  const lowQuality = pending.length - autoPublish
  return { total: pending.length, autoPublish, lowQuality }
})

/** 把后端 scoreBreakdown 转成模板渲染用的数组（label 为中文）。 */
function scoreSegments(bd: NonNullable<ApiLibraryImportItem['scoreBreakdown']>) {
  const tone = (seg: { score: number; maxScore: number }) => {
    const pct = seg.maxScore > 0 ? seg.score / seg.maxScore : 0
    if (pct >= 0.85) return '#22c55e'
    if (pct >= 0.5) return '#f59e0b'
    return '#ef4444'
  }
  return [
    { label: '音质', score: bd.quality.score, max: bd.quality.maxScore, pct: (bd.quality.score / bd.quality.maxScore) * 100, reason: bd.quality.reason, color: tone(bd.quality) },
    { label: '元数据', score: bd.metadata.score, max: bd.metadata.maxScore, pct: (bd.metadata.score / bd.metadata.maxScore) * 100, reason: bd.metadata.reason, color: tone(bd.metadata) },
    { label: '歌词', score: bd.lyrics.score, max: bd.lyrics.maxScore, pct: (bd.lyrics.score / bd.lyrics.maxScore) * 100, reason: bd.lyrics.reason, color: tone(bd.lyrics) },
  ]
}

async function loadItems() {
  loading.value = true
  try {
    const result = await listImportItems(
      activeStatus.value || undefined,
      page.value,
      pageSize.value,
      sortField.value,
      sortOrder.value,
      keyword.value.trim() || undefined,
    )
    items.value = result.items.map((row) => row.item)
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '导入列表加载失败'))
  } finally {
    loading.value = false
  }
}

/** 列头排序：prop 为列绑定的字段名，order 为 ascending/descending（取消排序回落到创建时间倒序）。 */
function handleSortChange({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }) {
  const fieldMap: Record<string, string> = {
    title: 'title',
    status: 'status',
    score: 'score',
    duration: 'duration',
    createdAt: 'created',
  }
  sortField.value = order ? fieldMap[prop] ?? 'created' : 'created'
  sortOrder.value = order === 'ascending' ? 'asc' : 'desc'
  page.value = 1
  void loadItems()
}

function changeTab() {
  page.value = 1
  void loadItems()
}

/** 关键词/状态查询：回到第一页重新拉取。 */
function search() {
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

/** 一键发布所有自动发布候选（分数 ≥ 75 的 pending_review 条目）。 */
async function handleBulkPublish() {
  const candidateCount = pendingScoreStats.value.autoPublish
  if (candidateCount === 0) {
    ElMessage.info('当前没有可一键发布的高分条目')
    return
  }
  try {
    await ElMessageBox.confirm(
      `将一键发布 ${candidateCount} 条高分待审核曲目（分数 ≥ 75，非用户上传，转码已完成）。低分曲目（< 75）不受影响。`,
      '一键发布高分曲目',
      {
        confirmButtonText: '确认发布',
        cancelButtonText: '取消',
        type: 'warning',
      },
    )
  } catch {
    return // 用户取消
  }

  bulkPublishing.value = true
  try {
    const result = await bulkPublishCandidates()
    if (result.success === result.total) {
      ElMessage.success(`全部 ${result.total} 条发布成功！`)
    } else {
      const failed = result.items.filter(i => !i.published)
      ElMessage.warning({
        message: `发布完成：成功 ${result.success}/${result.total}。${failed.length} 条失败，详情请看控制台。`,
        duration: 5000,
      })
      // 失败明细只在 console 打，不堆 UI
      failed.forEach(f => console.warn(`[bulk-publish 失败] ${f.displayName}`, f.error))
    }
    await loadItems()
  } catch (error) {
    ElMessage.error(describeError(error, '一键发布失败'))
  } finally {
    bulkPublishing.value = false
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

      <div class="toolbar">
        <el-input
          v-model="keyword"
          placeholder="搜索曲名 / 艺人 / 专辑"
          clearable
          class="keyword-input"
          @keyup.enter="search"
          @clear="search"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-button type="primary" :icon="Search" @click="search">查询</el-button>
        <el-select
          v-model="activeStatus"
          placeholder="状态筛选"
          class="status-select"
          @change="changeTab"
        >
          <el-option label="全部状态" value="" />
          <el-option v-for="(label, value) in statusText" :key="value" :label="label" :value="value" />
        </el-select>
        <span class="text-faint sort-hint">点击列头可排序：曲名 / 状态 / 评分</span>
      </div>

      <!-- 分段统计提示：仅在全部/pending_review tab 下显示 -->
      <div
        v-if="(!activeStatus || activeStatus === 'pending_review') && pendingScoreStats.total > 0"
        class="score-hint"
      >
        <el-icon size="14"><InfoFilled /></el-icon>
        <span class="score-hint-text">
          当前待审核 <b>{{ pendingScoreStats.total }}</b> 条 ·
          <span v-if="pendingScoreStats.autoPublish > 0" class="hint-good">
            {{ pendingScoreStats.autoPublish }} 条高分可一键发布
          </span>
          <span v-if="pendingScoreStats.autoPublish > 0 && pendingScoreStats.lowQuality > 0"> · </span>
          <span v-if="pendingScoreStats.lowQuality > 0" class="hint-warn">
            {{ pendingScoreStats.lowQuality }} 条低质量需人工处理
          </span>
        </span>
        <el-button
          v-if="pendingScoreStats.autoPublish > 0"
          size="small"
          type="primary"
          :loading="bulkPublishing"
          class="bulk-publish-btn"
          @click="handleBulkPublish"
        >
          一键发布高分
        </el-button>
      </div>

      <el-table
        v-loading="loading"
        :data="items"
        row-key="id"
        empty-text="当前状态暂无条目"
        @sort-change="handleSortChange"
      >
        <el-table-column label="文件 / 曲名" prop="title" sortable="custom" min-width="230">
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
        <el-table-column label="状态" prop="status" sortable="custom" width="120">
          <template #default="{ row }">
            <div class="status-cell">
              <el-tag size="small" :type="(statusTone[row.status] as any) || 'info'">
                {{ statusText[row.status] || row.status }}
              </el-tag>
              <el-tag
                v-if="row.status === 'published' && row.autoPublished"
                size="small"
                type="success"
                effect="plain"
                class="auto-publish-badge"
              >自动</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="评分" prop="score" sortable="custom" width="90" align="center">
          <template #default="{ row }">
            <template v-if="row.autoScore != null">
              <span
                class="score-value"
                :style="{ color: scoreTone(row.autoScore).color }"
                :title="`自动评分 ${row.autoScore}/100 · ${scoreTone(row.autoScore).label}`"
              >
                {{ row.autoScore }}
              </span>
              <span class="score-label" :style="{ color: scoreTone(row.autoScore).color }">
                {{ scoreTone(row.autoScore).label }}
              </span>
            </template>
            <span v-else class="text-faint">—</span>
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
          :page-sizes="[100, 150, 200, 500, 1000]"
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

        <!-- 自动评分面板 -->
        <div v-if="current.autoScore != null || current.scoreBreakdown" class="score-card">
          <div class="score-card-header">
            <span class="score-card-title">
              自动评分
              <el-tag
                v-if="current.autoPublished"
                size="small"
                type="success"
                effect="plain"
                class="auto-publish-badge"
              >已自动发布</el-tag>
            </span>
            <span
              v-if="current.autoScore != null"
              class="score-big"
              :style="{ color: scoreTone(current.autoScore).color }"
            >{{ current.autoScore }}</span>
          </div>

          <template v-if="current.scoreBreakdown">
            <div class="score-segments">
              <div
                v-for="seg in scoreSegments(current.scoreBreakdown)"
                :key="seg.label"
                class="score-seg"
              >
                <div class="score-seg-head">
                  <span class="score-seg-label">{{ seg.label }}</span>
                  <span class="score-seg-val">{{ seg.score }}/{{ seg.max }}</span>
                </div>
                <div class="score-seg-bar">
                  <div
                    class="score-seg-fill"
                    :style="{ width: seg.pct + '%', background: seg.color }"
                  />
                </div>
                <div v-if="seg.reason" class="score-seg-reason">{{ seg.reason }}</div>
              </div>
            </div>
          </template>
          <div v-else class="score-no-breakdown">（详细分项数据未返回，可能是历史导入未重新评分）</div>
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

/* === 状态列：自动发布标记 === */
.status-cell {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.auto-publish-badge {
  font-size: 11px;
  padding: 0 4px;
  height: 18px;
  line-height: 16px;
  opacity: 0.85;
}

/* === 评分列 === */
.score-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-weight: 600;
  font-size: 14px;
}

.score-label {
  font-size: 11px;
  margin-left: 2px;
  opacity: 0.85;
}

/* === 分段统计提示条 === */
.score-hint {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: -4px 0 12px;
  padding: 6px 12px;
  background: var(--adm-bg-soft);
  border-radius: 6px;
  font-size: 12px;
  color: var(--adm-text-2);
}

.score-hint-text { flex: 1; }

.toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.status-select { width: 200px; }

.keyword-input { width: 280px; }

.sort-hint { font-size: 12px; }

.bulk-publish-btn {
  margin-left: auto;
}

.hint-good { color: #22c55e; font-weight: 500; }
.hint-warn { color: #f59e0b; font-weight: 500; }

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

/* === 自动评分面板 === */
.score-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px 16px;
  margin-bottom: 20px;
  border: 1px solid var(--adm-border);
  border-radius: 8px;
  background: linear-gradient(180deg, var(--adm-bg-soft, #fafbfc), transparent 70%);
}

.score-card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.score-card-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--adm-text-1);
  display: flex;
  align-items: center;
  gap: 6px;
}

.score-big {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-weight: 700;
  font-size: 28px;
  line-height: 1;
  letter-spacing: -0.02em;
}

.score-segments {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.score-seg {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.score-seg-head {
  display: flex;
  justify-content: space-between;
  font-size: 12px;
}

.score-seg-label {
  color: var(--adm-text-1);
  font-weight: 500;
}

.score-seg-val {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  color: var(--adm-text-2);
}

.score-seg-bar {
  height: 6px;
  background: var(--adm-border);
  border-radius: 3px;
  overflow: hidden;
}

.score-seg-fill {
  height: 100%;
  border-radius: 3px;
  transition: width 0.3s ease;
}

.score-seg-reason {
  font-size: 11px;
  color: var(--adm-text-3);
  line-height: 1.4;
}

.score-no-breakdown {
  font-size: 12px;
  color: var(--adm-text-3);
  font-style: italic;
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
