<script setup lang="ts">
/**
 * 曲库管理：检索/筛选曲目，编辑元数据与封面，维护多语言歌词，删除曲目。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type UploadRequestOptions } from 'element-plus'
import { Delete, Edit, Headset, Plus, Refresh, Search, Upload } from '@element-plus/icons-vue'
import {
  createTrackLyric,
  deleteAdminTrack,
  deleteLyric,
  getAdminTracks,
  getTrackLyrics,
  updateAdminTrack,
  updateLyric,
} from '@/services/admin-catalog'
import { uploadArtwork } from '@/services/admin-artwork'
import { resolveUrl } from '@/services/http'
import { describeError } from '@/services/errors'
import { formatTime } from '@/utils/format'
import type { ApiLyric, ApiTrack } from '@/types/music'

const loading = ref(false)
const tracks = ref<ApiTrack[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(100)
const query = ref('')
const missingMetadata = ref(false)
const duplicates = ref(false)

const drawerOpen = ref(false)
const editing = ref<ApiTrack | null>(null)
const saving = ref(false)
const coverUploading = ref(false)
const form = reactive({
  title: '',
  artists: '',
  trackNumber: '' as string | number,
  discNumber: '' as string | number,
  releaseYear: '' as string | number,
  genres: '',
  artworkUrl: '' as string | null,
  artworkId: '' as string | null,
})

const lyrics = ref<ApiLyric[]>([])
const lyricsLoading = ref(false)
const lyricDialogOpen = ref(false)
const lyricSaving = ref(false)
const lyricForm = reactive({
  id: '',
  language: '',
  format: 'plain' as 'plain' | 'lrc',
  content: '',
  isDefault: false,
})

const artistText = (track: ApiTrack) => track.artists.map((artist) => artist.name).join('、') || '未知艺人'

async function loadTracks() {
  loading.value = true
  try {
    const result = await getAdminTracks({
      q: query.value,
      missingMetadata: missingMetadata.value,
      duplicates: duplicates.value,
      page: page.value,
      pageSize: pageSize.value,
    })
    tracks.value = [...result.items]
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '曲目列表加载失败'))
  } finally {
    loading.value = false
  }
}

function search() {
  page.value = 1
  void loadTracks()
}

function openEditor(track: ApiTrack) {
  editing.value = track
  form.title = track.title
  form.artists = track.artists.map((artist) => artist.name).join('、')
  form.trackNumber = track.trackNumber ?? ''
  form.discNumber = track.discNumber ?? ''
  form.releaseYear = track.releaseYear ?? ''
  form.genres = track.genres.join('、')
  form.artworkUrl = track.artworkUrl ?? ''
  form.artworkId = ''
  drawerOpen.value = true
  void loadLyrics(track.id)
}

function splitValues(value: string) {
  return [...new Set(value.split(/[、,，]/).map((item) => item.trim()).filter(Boolean))]
}

function optionalNumber(value: string | number) {
  const text = String(value).trim()
  if (!text) return null
  const parsed = Number(text)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null
}

async function saveTrack() {
  const track = editing.value
  if (!track || saving.value) return
  if (!form.title.trim() || !form.artists.trim()) {
    ElMessage.warning('曲名与艺人不能为空')
    return
  }
  saving.value = true
  try {
    const updated = await updateAdminTrack(track.id, {
      title: form.title.trim(),
      artists: splitValues(form.artists),
      trackNumber: optionalNumber(form.trackNumber),
      discNumber: optionalNumber(form.discNumber),
      releaseYear: optionalNumber(form.releaseYear),
      genres: splitValues(form.genres),
      ...(form.artworkId ? { artworkId: form.artworkId } : {}),
    })
    const index = tracks.value.findIndex((item) => item.id === updated.id)
    if (index >= 0) tracks.value.splice(index, 1, updated)
    editing.value = updated
    form.artworkUrl = updated.artworkUrl ?? ''
    form.artworkId = ''
    ElMessage.success('曲目已保存')
  } catch (error) {
    ElMessage.error(describeError(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function handleCoverUpload(options: UploadRequestOptions) {
  const track = editing.value
  if (!track || coverUploading.value) return
  coverUploading.value = true
  try {
    const result = await uploadArtwork(options.file)
    form.artworkUrl = result.artworkUrl
    form.artworkId = result.id
    const updated = await updateAdminTrack(track.id, { artworkId: result.id })
    const index = tracks.value.findIndex((item) => item.id === updated.id)
    if (index >= 0) tracks.value.splice(index, 1, updated)
    editing.value = updated
    ElMessage.success('封面已更新')
  } catch (error) {
    ElMessage.error(describeError(error, '封面上传失败'))
  } finally {
    coverUploading.value = false
  }
}

async function removeTrack(track: ApiTrack) {
  try {
    await ElMessageBox.confirm(`确定删除曲目「${track.title}」吗？音频与封面文件会一并删除。`, '删除曲目', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await deleteAdminTrack(track.id)
    ElMessage.success('曲目已删除')
    if (editing.value?.id === track.id) drawerOpen.value = false
    await loadTracks()
  } catch (error) {
    ElMessage.error(describeError(error, '删除失败'))
  }
}

// ===== 歌词 =====

async function loadLyrics(trackId: string) {
  lyricsLoading.value = true
  try {
    lyrics.value = await getTrackLyrics(trackId)
  } catch {
    lyrics.value = []
  } finally {
    lyricsLoading.value = false
  }
}

function resetLyricForm() {
  lyricForm.id = ''
  lyricForm.language = ''
  lyricForm.format = 'plain'
  lyricForm.content = ''
  lyricForm.isDefault = false
}

function openLyricDialog(lyric?: ApiLyric) {
  if (lyric) {
    lyricForm.id = lyric.id
    lyricForm.language = lyric.language ?? ''
    lyricForm.format = lyric.format
    lyricForm.content = lyric.content
    lyricForm.isDefault = lyric.isDefault
  } else {
    resetLyricForm()
  }
  lyricDialogOpen.value = true
}

function importLyricFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = () => {
    const text = String(reader.result ?? '')
    lyricForm.content = text
    if (/^\s*\[\d{1,2}:\d{2}(\.\d{1,3})?\]/m.test(text)) lyricForm.format = 'lrc'
    ElMessage.success('歌词文件已读取')
  }
  reader.readAsText(file)
  input.value = ''
}

async function saveLyric() {
  const track = editing.value
  if (!track || lyricSaving.value) return
  if (!lyricForm.content.trim()) {
    ElMessage.warning('歌词内容不能为空')
    return
  }
  lyricSaving.value = true
  try {
    if (lyricForm.id) {
      await updateLyric(lyricForm.id, {
        language: lyricForm.language || null,
        format: lyricForm.format,
        content: lyricForm.content,
        isDefault: lyricForm.isDefault,
      })
    } else {
      await createTrackLyric(track.id, {
        language: lyricForm.language || null,
        format: lyricForm.format,
        content: lyricForm.content,
        isDefault: lyricForm.isDefault,
      })
    }
    ElMessage.success(lyricForm.id ? '歌词已更新' : '歌词已新增')
    lyricDialogOpen.value = false
    await loadLyrics(track.id)
  } catch (error) {
    ElMessage.error(describeError(error, '歌词保存失败'))
  } finally {
    lyricSaving.value = false
  }
}

async function removeLyric(lyric: ApiLyric) {
  try {
    await ElMessageBox.confirm('确定删除这份歌词吗？', '删除歌词', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await deleteLyric(lyric.id)
    ElMessage.success('歌词已删除')
    if (editing.value) await loadLyrics(editing.value.id)
  } catch (error) {
    ElMessage.error(describeError(error, '删除失败'))
  }
}

const coverPreview = computed(() => (form.artworkUrl ? resolveUrl(form.artworkUrl) : ''))

onMounted(loadTracks)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>曲库管理</h2>
        <p>检索、修正曲目元数据，维护封面与歌词</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="loadTracks">刷新</el-button>
    </div>

    <div class="panel">
      <div class="filter-bar">
        <el-input
          v-model="query"
          placeholder="搜索曲名 / 艺人 / 专辑"
          clearable
          style="width: 280px"
          @keyup.enter="search"
          @clear="search"
        >
          <template #prefix><el-icon><Search /></el-icon></template>
        </el-input>
        <el-checkbox v-model="missingMetadata" @change="search">仅缺元数据</el-checkbox>
        <el-checkbox v-model="duplicates" @change="search">仅重复项</el-checkbox>
        <el-button type="primary" :icon="Search" @click="search">查询</el-button>
        <span class="text-muted total">共 {{ total }} 首</span>
      </div>

      <el-table
        v-loading="loading"
        :data="tracks"
        style="width: 100%; margin-top: 16px"
        row-key="id"
        empty-text="没有匹配的曲目"
      >
        <el-table-column label="封面" width="76">
          <template #default="{ row }">
            <div class="cover">
              <img v-if="row.artworkUrl" :src="resolveUrl(row.artworkUrl)" alt="" />
              <el-icon v-if="!row.artworkUrl"><Headset /></el-icon>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="曲名" min-width="200">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title }}</strong>
              <span class="text-faint mono">{{ row.id }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="艺人" min-width="150">
          <template #default="{ row }">{{ artistText(row) }}</template>
        </el-table-column>
        <el-table-column label="专辑" min-width="150">
          <template #default="{ row }">{{ row.album?.title || '—' }}</template>
        </el-table-column>
        <el-table-column label="时长" width="90">
          <template #default="{ row }">{{ formatTime((row.durationMs ?? 0) / 1000) }}</template>
        </el-table-column>
        <el-table-column label="年份" width="80">
          <template #default="{ row }">{{ row.releaseYear ?? '—' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="150" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Edit" @click="openEditor(row)">编辑</el-button>
            <el-button link type="danger" :icon="Delete" @click="removeTrack(row)">删除</el-button>
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
          @current-change="loadTracks"
          @size-change="search"
        />
      </div>
    </div>

    <el-drawer v-model="drawerOpen" title="编辑曲目" size="560px" destroy-on-close>
      <template v-if="editing">
        <div class="cover-block">
          <div class="cover cover-lg">
            <img v-if="coverPreview" :src="coverPreview" alt="" />
            <el-icon v-if="!coverPreview"><Headset /></el-icon>
          </div>
          <div class="cover-ops">
            <el-upload
              :show-file-list="false"
              accept="image/*"
              :http-request="handleCoverUpload"
              :disabled="coverUploading"
            >
              <el-button :icon="Upload" :loading="coverUploading">更换封面</el-button>
            </el-upload>
            <span class="text-faint">支持 JPG / PNG / WebP，上传后立即生效</span>
          </div>
        </div>

        <el-form label-position="top" class="edit-form">
          <el-form-item label="曲名" required>
            <el-input v-model="form.title" maxlength="300" />
          </el-form-item>
          <el-form-item label="艺人（用顿号或逗号分隔）" required>
            <el-input v-model="form.artists" placeholder="例如：周杰伦、方文山" />
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
          <el-form-item label="风格（用顿号或逗号分隔）">
            <el-input v-model="form.genres" placeholder="例如：流行、摇滚" />
          </el-form-item>
          <el-button type="primary" :loading="saving" @click="saveTrack">保存修改</el-button>
        </el-form>

        <el-divider content-position="left">歌词</el-divider>
        <div class="lyric-head">
          <span class="text-muted">共 {{ lyrics.length }} 份</span>
          <el-button size="small" :icon="Plus" @click="openLyricDialog()">新增歌词</el-button>
        </div>
        <div v-loading="lyricsLoading" class="lyric-list">
          <el-empty v-if="!lyricsLoading && lyrics.length === 0" description="暂无歌词" :image-size="64" />
          <div v-for="lyric in lyrics" :key="lyric.id" class="lyric-item">
            <div class="lyric-meta">
              <el-tag size="small" type="info">{{ lyric.language || '未指定语言' }}</el-tag>
              <el-tag size="small" :type="lyric.format === 'lrc' ? 'warning' : 'success'">
                {{ lyric.format === 'lrc' ? 'LRC' : '纯文本' }}
              </el-tag>
              <el-tag v-if="lyric.isDefault" size="small">默认</el-tag>
            </div>
            <p class="lyric-preview">{{ lyric.content }}</p>
            <div class="lyric-actions">
              <el-button link type="primary" @click="openLyricDialog(lyric)">编辑</el-button>
              <el-button link type="danger" @click="removeLyric(lyric)">删除</el-button>
            </div>
          </div>
        </div>
      </template>
    </el-drawer>

    <el-dialog v-model="lyricDialogOpen" :title="lyricForm.id ? '编辑歌词' : '新增歌词'" width="620px">
      <el-form label-position="top">
        <div class="form-row">
          <el-form-item label="语言">
            <el-input v-model="lyricForm.language" placeholder="zh / en 等，可留空" />
          </el-form-item>
          <el-form-item label="格式">
            <el-radio-group v-model="lyricForm.format">
              <el-radio-button value="plain">纯文本</el-radio-button>
              <el-radio-button value="lrc">LRC</el-radio-button>
            </el-radio-group>
          </el-form-item>
          <el-form-item label="设为默认">
            <el-switch v-model="lyricForm.isDefault" />
          </el-form-item>
        </div>
        <el-form-item label="歌词内容">
          <el-input
            v-model="lyricForm.content"
            type="textarea"
            :rows="12"
            :placeholder="lyricForm.format === 'lrc' ? '[00:12.34]歌词内容' : '每行一句歌词'"
          />
        </el-form-item>
        <el-form-item>
          <label class="file-pick">
            <el-button :icon="Upload" size="small">从 .lrc / .txt 导入</el-button>
            <input type="file" accept=".lrc,.txt" @change="importLyricFile" />
          </label>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="lyricDialogOpen = false">取消</el-button>
        <el-button type="primary" :loading="lyricSaving" @click="saveLyric">
          {{ lyricForm.id ? '保存' : '新增' }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.total {
  margin-left: auto;
  font-size: 13px;
}

.cover {
  display: grid;
  place-items: center;
  width: 42px;
  height: 42px;
  overflow: hidden;
  border-radius: 6px;
  background: #f1f4f8;
  color: #98a4b3;
}

.cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cover-lg {
  width: 96px;
  height: 96px;
  border-radius: 10px;
  font-size: 26px;
}

.title-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.title-cell strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cover-block {
  display: flex;
  align-items: center;
  gap: 18px;
  margin-bottom: 22px;
}

.cover-ops {
  display: flex;
  flex-direction: column;
  gap: 8px;
  font-size: 12px;
}

.form-row {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

.lyric-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}

.lyric-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-height: 60px;
}

.lyric-item {
  padding: 12px;
  border: 1px solid var(--adm-border);
  border-radius: 8px;
  background: #fafbfc;
}

.lyric-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
}

.lyric-preview {
  display: -webkit-box;
  overflow: hidden;
  margin: 0;
  color: var(--adm-text-2);
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 4;
}

.lyric-actions {
  display: flex;
  justify-content: flex-end;
}

.file-pick input {
  display: none;
}
</style>
