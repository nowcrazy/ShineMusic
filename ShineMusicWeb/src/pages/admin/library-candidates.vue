<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Refresh, Headset } from '@element-plus/icons-vue'
import type { ApiLibraryWorkflowReview, ApiLibraryWorkflowSettings } from '@/types/api.generated'
import { describeError } from '@/services/errors'
import { fetchAdminLibraryCandidateAudio, getAdminLibraryCandidateSettings, getAdminLibraryCandidates, resolveAdminLibraryCandidate } from '@/services/library-workflow'

const status = ref('pending')
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)
const rows = ref<ApiLibraryWorkflowReview[]>([])
const settings = ref<ApiLibraryWorkflowSettings | null>(null)
const settingsError = ref('')
const loading = ref(false)
const errorMessage = ref('')
const resolvingId = ref('')
const previewId = ref('')
const previewUrl = ref('')
const previewLoading = ref(false)
const previewError = ref('')
let listGeneration = 0
let previewGeneration = 0
let disposed = false

const statusText: Record<string, string> = { pending: '待审核', approved: '已通过', rejected: '已拒绝', expired: '已到期' }
const statusTone: Record<string, 'warning' | 'success' | 'info' | 'danger'> = { pending: 'warning', approved: 'success', rejected: 'danger', expired: 'info' }

function dateText(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}

async function loadRows() {
  const generation = ++listGeneration
  loading.value = true
  errorMessage.value = ''
  try {
    let result = await getAdminLibraryCandidates(status.value, page.value, pageSize.value)
    if (disposed || generation !== listGeneration) return
    const lastPage = Math.max(1, Math.ceil(result.total / pageSize.value))
    if (page.value > lastPage) {
      page.value = lastPage
      result = await getAdminLibraryCandidates(status.value, page.value, pageSize.value)
      if (disposed || generation !== listGeneration) return
    }
    rows.value = [...result.items]
    total.value = result.total
  } catch (error) {
    if (disposed || generation !== listGeneration) return
    errorMessage.value = describeError(error, '候选列表加载失败')
  } finally {
    if (generation === listGeneration) loading.value = false
  }
}

async function loadSettings() {
  settingsError.value = ''
  try {
    settings.value = await getAdminLibraryCandidateSettings()
  } catch (error) {
    settingsError.value = describeError(error, '审核配置加载失败')
  }
}

function changeFilter() {
  page.value = 1
  stopPreview()
  void loadRows()
}

function stopPreview() {
  previewGeneration++
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
  previewUrl.value = ''
  previewId.value = ''
  previewLoading.value = false
  previewError.value = ''
}

async function audition(row: ApiLibraryWorkflowReview) {
  stopPreview()
  const generation = previewGeneration
  previewId.value = row.id
  previewLoading.value = true
  try {
    const blob = await fetchAdminLibraryCandidateAudio(row.id)
    if (disposed || generation !== previewGeneration) return
    previewUrl.value = URL.createObjectURL(blob)
  } catch (error) {
    if (disposed || generation !== previewGeneration) return
    previewError.value = describeError(error, error instanceof Error ? error.message : '试听加载失败')
  } finally {
    if (generation === previewGeneration) previewLoading.value = false
  }
}

async function resolve(row: ApiLibraryWorkflowReview, decision: 'approve' | 'separate' | 'reject') {
  if (resolvingId.value) return
  let reason = ''
  try {
    const result = await ElMessageBox.prompt(
      decision === 'approve'
        ? `确认将「${row.title}」加入公共曲库？已有公共版本时仅保留质量更好的版本。请记录通过依据。`
        : decision === 'separate' ? `请先确认「${row.title}」是不同录音（例如现场版、翻唱或重录），再作为另一公共曲目新增；不能用于保留同录音的重复版本。请填写判定依据。`
        : `拒绝「${row.title}」的公开申请不会删除上传者的私人原件。请填写原因。`,
      decision === 'approve' ? '通过公开审核' : decision === 'separate' ? '确认不同录音' : '拒绝公开申请',
      { confirmButtonText: decision === 'approve' ? '确认通过' : decision === 'separate' ? '确认并单独新增' : '确认拒绝', cancelButtonText: '取消', inputType: 'textarea', inputValidator: (value: string) => value.trim().length > 0 || '请填写审核理由' },
    )
    reason = result.value.trim()
  } catch {
    return
  }
  resolvingId.value = row.id
  try {
    await resolveAdminLibraryCandidate(row.id, decision, reason)
    if (previewId.value === row.id) stopPreview()
    ElMessage.success(decision === 'reject' ? '已拒绝公开申请' : '审核已通过')
    await loadRows()
  } catch (error) {
    ElMessage.error(describeError(error, '审核失败，请刷新列表后重试'))
  } finally {
    resolvingId.value = ''
  }
}

onMounted(() => { void loadRows(); void loadSettings() })
onBeforeUnmount(() => { disposed = true; listGeneration++; stopPreview() })
</script>

<template>
  <div class="candidates-page">
    <div class="heading">
      <div><h2>公共曲库待选</h2><p>仅显示上传者主动参与公开审核、且达到候选条件的原件。通过后公共库同一歌曲只保留最高质量版本。</p></div>
      <el-button :icon="Refresh" :loading="loading" @click="loadRows">刷新</el-button>
    </div>
    <el-alert title="审核只决定公共收录。拒绝或到期均不删除上传者的私人歌曲。到期时间采用服务器当前配置。" type="info" :closable="false" show-icon />
    <el-alert v-if="settingsError" class="settings-error" :title="settingsError" type="error" :closable="false" show-icon />
    <div v-if="settings" class="settings-strip" aria-label="服务器审核配置">
      <span>私人确认 {{ settings.privateReviewDays }} 天</span>
      <span>公开候选 {{ settings.publicReviewDays }} 天</span>
      <span>回收站 {{ settings.recycleDays }} 天</span>
      <span>MP3 ≥ {{ settings.mp3MinimumKbps }} kbps</span>
      <span>AAC-LC ≥ {{ settings.aacLcMinimumKbps }} kbps</span>
      <span>Opus ≥ {{ settings.opusMinimumKbps }} kbps</span>
      <span>Vorbis ≥ {{ settings.vorbisMinimumKbps }} kbps</span>
    </div>
    <div class="toolbar">
      <el-radio-group v-model="status" @change="changeFilter">
        <el-radio-button value="pending">待审核</el-radio-button><el-radio-button value="approved">已通过</el-radio-button><el-radio-button value="rejected">已拒绝</el-radio-button><el-radio-button value="expired">已到期</el-radio-button><el-radio-button value="">全部</el-radio-button>
      </el-radio-group>
    </div>
    <el-alert v-if="errorMessage" :title="errorMessage" type="error" :closable="false" show-icon />
    <div class="table-card">
      <el-table v-loading="loading" :data="rows" row-key="id" empty-text="当前没有候选条目">
        <el-table-column label="歌曲 / 艺人" min-width="210"><template #default="{ row }"><div class="track-copy"><strong>{{ row.title || '未命名' }}</strong><span>{{ row.artists.join(' / ') || '—' }}</span><small>{{ row.albumTitle || '—' }}</small></div></template></el-table-column>
        <el-table-column label="原件音质" min-width="240"><template #default="{ row }"><div class="track-copy"><strong>{{ row.originalCodec || '未知编码' }} <span>{{ row.codecProfile || '' }}</span></strong><span>{{ row.originalContainer || '—' }} · {{ row.originalBitrateKbps ?? '—' }} kbps</span><span>{{ row.sampleRateHz ?? '—' }} Hz · {{ row.bitsPerSample ?? '—' }} bit · {{ row.channels ?? '—' }} 声道</span></div></template></el-table-column>
        <el-table-column label="公共库对比" min-width="230"><template #default="{ row }"><div v-if="row.existingTrackId" class="track-copy"><strong>{{ row.existingTrackTitle }}</strong><span>{{ row.existingTrackArtists?.join(' / ') || '—' }}</span><span>{{ row.existingCodec || '未知编码' }} · {{ row.existingBitrateKbps ?? '—' }} kbps</span><small>已收录；通过时由服务器重新校验升级条件</small></div><span v-else class="text-muted">公共库尚无匹配版本</span></template></el-table-column>
        <el-table-column label="状态 / 到期" min-width="190"><template #default="{ row }"><div class="track-copy"><el-tag :type="statusTone[row.status] || 'info'">{{ statusText[row.status] || row.status }}</el-tag><span>{{ dateText(row.expiresAtUtc) }}</span></div></template></el-table-column>
        <el-table-column label="操作" fixed="right" min-width="220"><template #default="{ row }"><template v-if="row.status === 'pending'"><el-button link :icon="Headset" type="primary" :loading="previewId === row.id && previewLoading" @click="audition(row)">试听</el-button><el-button link type="success" :disabled="!!resolvingId" @click="resolve(row, 'approve')">通过</el-button><el-button v-if="row.reviewReason === 'public_comparison'" link :disabled="!!resolvingId" @click="resolve(row, 'separate')">确认不同录音</el-button><el-button link type="danger" :disabled="!!resolvingId" @click="resolve(row, 'reject')">拒绝</el-button></template><span v-else class="text-muted">{{ statusText[row.status] || row.status }}</span></template></el-table-column>
      </el-table>
      <div v-if="previewId" class="audition-bar"><strong>候选试听</strong><span v-if="previewLoading">正在安全加载…</span><audio v-if="previewUrl" :src="previewUrl" controls preload="metadata" @error="previewError = '音频播放失败，请重新加载试听'" /><span v-if="previewError" class="preview-error" role="alert">{{ previewError }}</span><el-button @click="stopPreview">关闭试听</el-button></div>
      <div class="pagination"><el-pagination v-model:current-page="page" v-model:page-size="pageSize" :total="total" :page-sizes="[20, 50, 100]" layout="total, sizes, prev, pager, next" @current-change="loadRows" @size-change="changeFilter" /></div>
    </div>
  </div>
</template>

<style scoped>
.heading { display: flex; justify-content: space-between; align-items: flex-start; gap: 20px; margin-bottom: 18px; }
.heading h2 { margin: 0 0 8px; color: var(--adm-text); font-size: 24px; }
.heading p { margin: 0; color: var(--adm-text-2); max-width: 850px; line-height: 1.6; }
.toolbar { margin: 22px 0 16px; overflow-x: auto; }
.settings-error { margin-top: 12px; }
.settings-strip { display: flex; flex-wrap: wrap; gap: 8px 20px; padding: 12px 0; color: var(--adm-text-2); font-size: 12px; line-height: 1.5; }
.table-card { margin-top: 16px; padding: 16px; background: #fff; border: 1px solid var(--adm-border); border-radius: 14px; }
.track-copy { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; padding: 6px 0; line-height: 1.45; overflow-wrap: anywhere; }
.track-copy span, .track-copy small, .text-muted { color: var(--adm-text-2); }
.track-copy small { font-size: 12px; }
.audition-bar { display: flex; align-items: center; gap: 16px; padding: 18px; margin-top: 16px; border-radius: 10px; background: var(--adm-primary-soft); flex-wrap: wrap; }
.audition-bar audio { flex: 1; min-width: 240px; height: 40px; }
.preview-error { color: var(--adm-danger); }
.pagination { display: flex; justify-content: flex-end; padding-top: 20px; overflow-x: auto; }
@media (max-width: 700px) { .heading { flex-direction: column; } .table-card { padding: 8px; } }
</style>
