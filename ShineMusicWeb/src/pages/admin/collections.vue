<script setup lang="ts">
/**
 * 运营歌单：创建/编辑精选歌单，上传封面，按顺序编排曲目。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type UploadRequestOptions } from 'element-plus'
import { ArrowDown, ArrowUp, Delete, Edit, Plus, Refresh, Search, Upload } from '@element-plus/icons-vue'
import {
  createCollection,
  deleteCollection,
  getCollectionDetail,
  listCollections,
  replaceCollectionTracks,
  searchTracksForCollection,
  updateCollection,
} from '@/services/admin-collections'
import { uploadArtwork } from '@/services/admin-artwork'
import { resolveUrl } from '@/services/http'
import { describeError } from '@/services/errors'
import { formatTime } from '@/utils/format'
import type { ApiCollectionSummary, ApiTrack } from '@/types/music'

const loading = ref(false)
const items = ref<ApiCollectionSummary[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)

const dialogOpen = ref(false)
const editingId = ref('')
const saving = ref(false)
const coverUploading = ref(false)
const form = reactive({
  title: '',
  description: '',
  isPublished: true,
  artworkId: '' as string,
  artworkUrl: '' as string,
})

const selectedTracks = ref<ApiTrack[]>([])
const keyword = ref('')
const searching = ref(false)
const candidates = ref<ApiTrack[]>([])

const artistText = (track: ApiTrack) => track.artists.map((artist) => artist.name).join('、') || '未知艺人'
const coverPreview = () => (form.artworkUrl ? resolveUrl(form.artworkUrl) : '')

async function load() {
  loading.value = true
  try {
    const result = await listCollections(page.value, pageSize.value)
    items.value = [...result.items]
    total.value = result.total
  } catch (error) {
    ElMessage.error(describeError(error, '歌单列表加载失败'))
  } finally {
    loading.value = false
  }
}

function resetForm() {
  form.title = ''
  form.description = ''
  form.isPublished = true
  form.artworkId = ''
  form.artworkUrl = ''
  selectedTracks.value = []
  candidates.value = []
  keyword.value = ''
}

function openCreate() {
  editingId.value = ''
  resetForm()
  dialogOpen.value = true
}

async function openEdit(item: ApiCollectionSummary) {
  editingId.value = item.id
  resetForm()
  form.title = item.title
  form.description = item.description ?? ''
  form.artworkUrl = item.artworkUrl ?? ''
  dialogOpen.value = true
  try {
    const detail = await getCollectionDetail(item.id)
    selectedTracks.value = [...detail.tracks]
    form.artworkUrl = detail.artworkUrl ?? form.artworkUrl
  } catch (error) {
    ElMessage.error(describeError(error, '歌单详情加载失败'))
  }
}

async function searchTracks() {
  searching.value = true
  try {
    const result = await searchTracksForCollection(keyword.value, 1, 20)
    candidates.value = [...result.items]
    if (candidates.value.length === 0) ElMessage.info('没有匹配的曲目')
  } catch (error) {
    ElMessage.error(describeError(error, '曲目搜索失败'))
  } finally {
    searching.value = false
  }
}

function addTrack(track: ApiTrack) {
  if (selectedTracks.value.some((item) => item.id === track.id)) {
    ElMessage.info('该曲目已在歌单中')
    return
  }
  selectedTracks.value.push(track)
}

function moveTrack(index: number, offset: number) {
  const target = index + offset
  if (target < 0 || target >= selectedTracks.value.length) return
  const list = [...selectedTracks.value]
  const [moved] = list.splice(index, 1)
  list.splice(target, 0, moved)
  selectedTracks.value = list
}

function removeTrack(index: number) {
  selectedTracks.value.splice(index, 1)
}

async function handleCoverUpload(options: UploadRequestOptions) {
  if (coverUploading.value) return
  coverUploading.value = true
  try {
    const result = await uploadArtwork(options.file)
    form.artworkId = result.id
    form.artworkUrl = result.artworkUrl
    ElMessage.success('封面已上传，保存后生效')
  } catch (error) {
    ElMessage.error(describeError(error, '封面上传失败'))
  } finally {
    coverUploading.value = false
  }
}

async function save() {
  if (saving.value) return
  if (!form.title.trim()) {
    ElMessage.warning('请填写歌单标题')
    return
  }
  const trackIds = selectedTracks.value.map((track) => track.id)
  saving.value = true
  try {
    if (editingId.value) {
      await updateCollection(editingId.value, {
        title: form.title.trim(),
        description: form.description.trim() || null,
        isPublished: form.isPublished,
        ...(form.artworkId ? { artworkId: form.artworkId } : {}),
      })
      await replaceCollectionTracks(editingId.value, trackIds)
      ElMessage.success('歌单已更新')
    } else {
      await createCollection({
        title: form.title.trim(),
        description: form.description.trim() || null,
        trackIds,
        ...(form.artworkId ? { artworkId: form.artworkId } : {}),
      })
      ElMessage.success('歌单已创建')
    }
    dialogOpen.value = false
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function remove(item: ApiCollectionSummary) {
  try {
    await ElMessageBox.confirm(`确定删除歌单「${item.title}」吗？曲目本身不受影响。`, '删除歌单', {
      confirmButtonText: '删除',
      cancelButtonText: '取消',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await deleteCollection(item.id)
    ElMessage.success('歌单已删除')
    await load()
  } catch (error) {
    ElMessage.error(describeError(error, '删除失败'))
  }
}

onMounted(load)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>运营歌单</h2>
        <p>编排精选合集，封面、推荐语与曲目顺序均可调整</p>
      </div>
      <div class="heading-actions">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openCreate">新建歌单</el-button>
      </div>
    </div>

    <div class="panel">
      <el-table v-loading="loading" :data="items" row-key="id" empty-text="暂无已发布歌单">
        <el-table-column label="封面" width="76">
          <template #default="{ row }">
            <div class="cover">
              <img v-if="row.artworkUrl" :src="resolveUrl(row.artworkUrl)" alt="" />
              <span v-else>♪</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="标题" min-width="200">
          <template #default="{ row }">
            <div class="title-cell">
              <strong>{{ row.title }}</strong>
              <span class="text-faint mono">{{ row.id }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="推荐语" min-width="260">
          <template #default="{ row }">
            <span class="text-muted">{{ row.description || '—' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="曲目数" width="90" prop="trackCount" />
        <el-table-column label="操作" width="150" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Edit" @click="openEdit(row)">编辑</el-button>
            <el-button link type="danger" :icon="Delete" @click="remove(row)">删除</el-button>
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
          @size-change="load"
        />
      </div>
    </div>

    <el-dialog
      v-model="dialogOpen"
      :title="editingId ? '编辑歌单' : '新建歌单'"
      width="900px"
      top="6vh"
      destroy-on-close
    >
      <div class="editor">
        <div class="editor-form">
          <div class="cover-block">
            <div class="cover cover-lg">
              <img v-if="form.artworkUrl" :src="coverPreview()" alt="" />
              <span v-else>♪</span>
            </div>
            <div class="cover-ops">
              <el-upload
                :show-file-list="false"
                accept="image/*"
                :http-request="handleCoverUpload"
                :disabled="coverUploading"
              >
                <el-button size="small" :icon="Upload" :loading="coverUploading">上传封面</el-button>
              </el-upload>
              <span class="text-faint">不填则自动使用首曲封面</span>
            </div>
          </div>

          <el-form label-position="top">
            <el-form-item label="标题" required>
              <el-input v-model="form.title" maxlength="100" show-word-limit />
            </el-form-item>
            <el-form-item label="推荐语">
              <el-input
                v-model="form.description"
                type="textarea"
                :rows="3"
                maxlength="500"
                show-word-limit
                placeholder="一句话推荐这个歌单"
              />
            </el-form-item>
            <el-form-item v-if="editingId" label="发布状态">
              <el-switch v-model="form.isPublished" active-text="已发布" inactive-text="草稿" />
              <div v-if="!form.isPublished" class="publish-hint">
                取消发布后歌单会从列表中隐藏（后台暂无草稿列表端点）
              </div>
            </el-form-item>
          </el-form>
        </div>

        <div class="editor-tracks">
          <div class="track-col">
            <div class="col-head">添加曲目</div>
            <el-input
              v-model="keyword"
              placeholder="搜索曲名 / 艺人"
              size="small"
              clearable
              @keyup.enter="searchTracks"
            >
              <template #append>
                <el-button :icon="Search" :loading="searching" @click="searchTracks" />
              </template>
            </el-input>
            <div class="candidate-list">
              <el-empty v-if="candidates.length === 0" description="搜索并添加曲目" :image-size="56" />
              <button
                v-for="track in candidates"
                :key="track.id"
                type="button"
                class="candidate"
                @click="addTrack(track)"
              >
                <div class="candidate-copy">
                  <strong>{{ track.title }}</strong>
                  <span>{{ artistText(track) }}</span>
                </div>
                <el-icon><Plus /></el-icon>
              </button>
            </div>
          </div>

          <div class="track-col">
            <div class="col-head">已选曲目（{{ selectedTracks.length }}）</div>
            <div class="selected-list">
              <el-empty v-if="selectedTracks.length === 0" description="尚未选择曲目" :image-size="56" />
              <div v-for="(track, index) in selectedTracks" :key="track.id" class="selected-row">
                <span class="order">{{ index + 1 }}</span>
                <div class="candidate-copy">
                  <strong>{{ track.title }}</strong>
                  <span>{{ artistText(track) }} · {{ formatTime((track.durationMs ?? 0) / 1000) }}</span>
                </div>
                <div class="row-ops">
                  <el-button link :icon="ArrowUp" :disabled="index === 0" @click="moveTrack(index, -1)" />
                  <el-button
                    link
                    :icon="ArrowDown"
                    :disabled="index === selectedTracks.length - 1"
                    @click="moveTrack(index, 1)"
                  />
                  <el-button link type="danger" :icon="Delete" @click="removeTrack(index)" />
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <template #footer>
        <el-button @click="dialogOpen = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存歌单</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  gap: 10px;
}

.cover {
  display: grid;
  place-items: center;
  width: 44px;
  height: 44px;
  overflow: hidden;
  border-radius: 8px;
  background: #f1f4f8;
  color: #98a4b3;
  font-size: 18px;
}

.cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cover-lg {
  width: 96px;
  height: 96px;
  font-size: 28px;
}

.title-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.editor {
  display: grid;
  grid-template-columns: 320px 1fr;
  gap: 24px;
}

.cover-block {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 18px;
}

.cover-ops {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 12px;
}

.publish-hint {
  margin-top: 6px;
  color: var(--adm-text-3);
  font-size: 12px;
}

.editor-tracks {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 16px;
  min-width: 0;
}

.col-head {
  margin-bottom: 10px;
  font-size: 13px;
  font-weight: 600;
}

.candidate-list,
.selected-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
  height: 380px;
  padding: 8px;
  margin-top: 10px;
  overflow-y: auto;
  border: 1px solid var(--adm-border);
  border-radius: 8px;
  background: #fbfcfd;
}

.candidate {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid transparent;
  border-radius: 6px;
  background: #ffffff;
  color: var(--adm-text);
  text-align: left;
  cursor: pointer;
}

.candidate:hover {
  border-color: var(--el-color-primary-light-5);
}

.candidate-copy {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  overflow: hidden;
}

.candidate-copy strong,
.candidate-copy span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.candidate-copy strong {
  font-size: 13px;
  font-weight: 600;
}

.candidate-copy span {
  color: var(--adm-text-2);
  font-size: 11px;
}

.selected-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid var(--adm-border);
  border-radius: 6px;
  background: #ffffff;
}

.selected-row .candidate-copy {
  flex: 1;
}

.order {
  flex: none;
  width: 20px;
  color: var(--adm-text-3);
  font-size: 12px;
  text-align: center;
}

.row-ops {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
}
</style>
