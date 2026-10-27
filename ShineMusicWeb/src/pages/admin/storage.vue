<script setup lang="ts">
/**
 * 存储治理：审计数据库引用与磁盘文件的一致性。
 * - 指标卡可点击，跳转到对应明细页签；
 * - 「曲目文件」按曲目分组聚合音频与封面，展开可查看/预览每个文件；
 * - 图片（封面）支持查看大图，音频支持在线试听，孤儿文件同样可预览。
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Delete, Folder, Headset, Picture, Refresh, Search, View } from '@element-plus/icons-vue'
import { cleanupStorage, fetchStorageFileBlob, getStorageAudit } from '@/services/admin-storage'
import { describeError } from '@/services/errors'
import { formatBytes, formatTime } from '@/utils/format'
import type { ApiStorageAudit, ApiStorageFileGroup, ApiStorageGroupFile } from '@/types/api.generated'

type TabName = 'groups' | 'orphans' | 'missing' | 'dirs'

const loading = ref(false)
const cleaning = ref(false)
const audit = ref<ApiStorageAudit | null>(null)
const keyword = ref('')
const activeTab = ref<TabName>('groups')
const groupPage = ref(1)
const orphanPage = ref(1)
const missingPage = ref(1)
const listPageSize = ref(100)
const groupPageSize = ref(100)

/** 取存储键顶层目录（original/、artwork/ 等），无目录归入 (根)。 */
function topDir(key: string) {
  const index = key.indexOf('/')
  return index > 0 ? key.slice(0, index) : '(根目录)'
}

function extOf(key: string) {
  const index = key.lastIndexOf('.')
  return index > 0 ? key.slice(index + 1).toLowerCase() : ''
}

/** 目录分组统计：文件数 + 合计大小，按大小降序。 */
function groupByDir(rows: readonly { storageKey: string; sizeBytes: number }[]) {
  const groups = new Map<string, { dir: string; count: number; bytes: number }>()
  for (const row of rows) {
    const dir = topDir(row.storageKey)
    const entry = groups.get(dir) ?? { dir, count: 0, bytes: 0 }
    entry.count += 1
    entry.bytes += row.sizeBytes
    groups.set(dir, entry)
  }
  return [...groups.values()].sort((a, b) => b.bytes - a.bytes)
}

const metrics = computed(() => {
  const data = audit.value
  if (!data) return []
  return [
    { label: '在库歌曲', value: data.trackCount, hint: '每首对应原件 · 播放件 · 封面', tab: 'groups' as TabName },
    { label: '数据库引用文件', value: data.referencedFileCount, hint: '曲目音频、封面等登记在库的文件', tab: 'groups' as TabName },
    { label: '磁盘文件', value: data.diskFileCount, hint: '存储目录中实际存在的文件', tab: 'groups' as TabName },
    { label: '缺失文件', value: data.missingFileCount, hint: '库中有记录但磁盘不存在', tone: data.missingFileCount > 0 ? 'danger' : '', tab: 'missing' as TabName },
    { label: '孤儿文件', value: data.orphanFileCount, hint: '磁盘存在但库中无引用', tone: data.orphanFileCount > 0 ? 'warning' : '', tab: 'orphans' as TabName },
    { label: '空目录', value: data.emptyDirectoryCount, hint: '可安全删除的空目录', tab: 'dirs' as TabName },
    { label: '可回收空间', value: formatBytes(data.reclaimableBytes) || '0 KB', hint: '清理孤儿文件后可释放', tab: 'orphans' as TabName },
  ]
})

// ---------- 曲目分组 ----------

const filteredGroups = computed(() => {
  const rows = audit.value?.groups ?? []
  const kw = keyword.value.trim().toLowerCase()
  if (!kw) return [...rows]
  return rows.filter(
    (group) =>
      group.title.toLowerCase().includes(kw) ||
      (group.artists ?? '').toLowerCase().includes(kw) ||
      (group.albumTitle ?? '').toLowerCase().includes(kw),
  )
})

const groupPageRows = computed(() =>
  filteredGroups.value.slice((groupPage.value - 1) * groupPageSize.value, groupPage.value * groupPageSize.value))

function groupKeyOf(group: ApiStorageFileGroup) {
  return group.trackId ?? '__other__'
}

/** 组内第一个在盘的封面文件（用于缩略图与预览）。 */
function coverFileOf(group: ApiStorageFileGroup): ApiStorageGroupFile | undefined {
  return group.files.find((file) => file.kind === 'artwork' && file.onDisk)
}

function missingCountOf(group: ApiStorageFileGroup) {
  return group.files.filter((file) => !file.onDisk).length
}

function durationText(ms: number | null) {
  return ms && ms > 0 ? formatTime(ms / 1000) : '—'
}

// ---------- 孤儿 / 缺失 ----------

const filteredOrphans = computed(() => {
  const rows = [...(audit.value?.orphans ?? [])]
  const kw = keyword.value.trim().toLowerCase()
  const filtered = kw ? rows.filter((row) => row.storageKey.toLowerCase().includes(kw)) : rows
  return filtered.sort((a, b) => b.sizeBytes - a.sizeBytes)
})

const filteredMissing = computed(() => {
  const rows = [...(audit.value?.missing ?? [])]
  const kw = keyword.value.trim().toLowerCase()
  const filtered = kw
    ? rows.filter(
        (row) =>
          row.storageKey.toLowerCase().includes(kw) ||
          row.referencedBy.some((owner) => owner.toLowerCase().includes(kw)),
      )
    : rows
  return filtered
})

const orphanPageRows = computed(() =>
  filteredOrphans.value.slice((orphanPage.value - 1) * listPageSize.value, orphanPage.value * listPageSize.value))
const missingPageRows = computed(() =>
  filteredMissing.value.slice((missingPage.value - 1) * listPageSize.value, missingPage.value * listPageSize.value))

const orphanGroups = computed(() => groupByDir(filteredOrphans.value))

function switchTab(name: string) {
  activeTab.value = name as TabName
  groupPage.value = 1
  orphanPage.value = 1
  missingPage.value = 1
}

// ---------- 文件预览（图片查看 / 音频试听） ----------

const previewVisible = ref(false)
const previewLoading = ref(false)
const previewKind = ref<'image' | 'audio'>('image')
const previewUrl = ref('')
const previewKey = ref('')
/** 当前预览 URL 是否为本对话框私有（私有的在关闭时 revoke；封面缩略图复用的不 revoke）。 */
let previewOwned = false

/** 封面缩略图缓存：storageKey -> objectURL（空串表示加载失败，避免重复拉取）。 */
const coverThumbs = reactive<Record<string, string>>({})
const ownedUrls = new Set<string>()

const previewFileName = computed(() => {
  const key = previewKey.value
  const index = key.lastIndexOf('/')
  return index >= 0 ? key.slice(index + 1) : key
})

/** 打开文件预览：封面复用缩略图缓存，其余现场拉取 blob。 */
async function openPreview(file: { storageKey: string; kind: string }) {
  if (file.kind !== 'artwork' && file.kind !== 'audio') {
    ElMessage.info('该文件类型不支持预览')
    return
  }
  previewKind.value = file.kind === 'artwork' ? 'image' : 'audio'
  previewKey.value = file.storageKey
  previewUrl.value = ''
  previewOwned = false
  previewVisible.value = true
  previewLoading.value = true
  try {
    const cached = file.kind === 'artwork' ? coverThumbs[file.storageKey] : undefined
    if (cached) {
      previewUrl.value = cached
      return
    }
    const blob = await fetchStorageFileBlob(file.storageKey)
    const url = URL.createObjectURL(blob)
    ownedUrls.add(url)
    previewOwned = true
    previewUrl.value = url
  } catch (error) {
    previewVisible.value = false
    ElMessage.error(describeError(error, '文件预览失败'))
  } finally {
    previewLoading.value = false
  }
}

function onPreviewClosed() {
  if (previewOwned && previewUrl.value) {
    ownedUrls.delete(previewUrl.value)
    URL.revokeObjectURL(previewUrl.value)
  }
  previewOwned = false
  previewUrl.value = ''
}

/** 加载曲目组的封面缩略图：列表页可见行在渲染时预取，展开行与点击封面时复用缓存。 */
async function ensureCoverThumb(group: ApiStorageFileGroup) {
  const cover = coverFileOf(group)
  if (!cover) return
  const key = cover.storageKey
  if (coverThumbs[key] !== undefined) return
  coverThumbs[key] = '' // 占位防重入；失败保持空串回退图标
  try {
    const blob = await fetchStorageFileBlob(key)
    const url = URL.createObjectURL(blob)
    ownedUrls.add(url)
    coverThumbs[key] = url
  } catch {
    // 缩略图加载失败不打扰：单元格回退为图标，明细里仍可点预览
  }
}

function onGroupExpand(group: ApiStorageFileGroup, expanded: ApiStorageFileGroup[]) {
  if (expanded.some((row) => groupKeyOf(row) === groupKeyOf(group))) void ensureCoverThumb(group)
}

async function onCoverClick(group: ApiStorageFileGroup) {
  const cover = coverFileOf(group)
  if (!cover) return
  await ensureCoverThumb(group)
  if (coverThumbs[cover.storageKey]) openPreview(cover)
  else ElMessage.error('封面加载失败')
}

// ---------- 审计与清理 ----------

async function runAudit() {
  loading.value = true
  try {
    audit.value = await getStorageAudit()
  } catch (error) {
    ElMessage.error(describeError(error, '存储审计失败'))
  } finally {
    loading.value = false
  }
}

async function cleanup() {
  const data = audit.value
  if (!data) return
  try {
    await ElMessageBox.confirm(
      `将删除 ${data.orphanFileCount} 个孤儿文件与 ${data.emptyDirectoryCount} 个空目录，预计释放 ${formatBytes(data.reclaimableBytes) || '0 KB'}。此操作不可撤销。`,
      '清理存储',
      { confirmButtonText: '立即清理', cancelButtonText: '取消', type: 'warning' },
    )
  } catch {
    return
  }
  cleaning.value = true
  try {
    const result = await cleanupStorage()
    ElMessage.success(
      `已删除 ${result.deletedOrphanCount} 个文件（${formatBytes(result.deletedBytes) || '0 KB'}），移除 ${result.removedEmptyDirectoryCount} 个空目录`,
    )
    await runAudit()
  } catch (error) {
    ElMessage.error(describeError(error, '清理失败'))
  } finally {
    cleaning.value = false
  }
}

onMounted(runAudit)

/** 当前页的曲目行渲染后立即预取封面，避免列表默认显示空白占位。 */
watch(
  groupPageRows,
  (rows) => {
    for (const group of rows) void ensureCoverThumb(group)
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  for (const url of ownedUrls) URL.revokeObjectURL(url)
  ownedUrls.clear()
})
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>存储治理</h2>
        <p>比对数据库引用与磁盘文件，按曲目聚合查看关联文件，回收无主文件与空目录</p>
      </div>
      <div class="heading-actions">
        <el-button :icon="Refresh" :loading="loading" @click="runAudit">重新审计</el-button>
        <el-button
          type="danger"
          :icon="Delete"
          :loading="cleaning"
          :disabled="!audit || (audit.orphanFileCount === 0 && audit.emptyDirectoryCount === 0)"
          @click="cleanup"
        >
          清理孤儿文件
        </el-button>
      </div>
    </div>

    <div class="metric-grid" v-loading="loading">
      <div
        v-for="metric in metrics"
        :key="metric.label"
        class="metric clickable"
        :class="[metric.tone, { active: activeTab === metric.tab }]"
        @click="switchTab(metric.tab)"
      >
        <span class="metric-label">{{ metric.label }}</span>
        <strong class="metric-value">{{ metric.value }}</strong>
        <span class="metric-hint">{{ metric.hint }}</span>
      </div>
      <el-empty v-if="!audit && !loading" description="尚未执行审计" :image-size="80" />
    </div>

    <template v-if="audit">
      <div class="panel">
        <div class="panel-title">
          明细
          <el-input
            v-model="keyword"
            placeholder="按曲名 / 艺术家 / 存储键过滤"
            size="small"
            clearable
            style="width: 260px"
          >
            <template #prefix><el-icon><Search /></el-icon></template>
          </el-input>
        </div>

        <el-tabs :model-value="activeTab" @tab-change="(name) => switchTab(String(name))">
          <el-tab-pane name="groups">
            <template #label>
              曲目文件
              <el-tag size="small" style="margin-left: 6px">{{ filteredGroups.length }}</el-tag>
            </template>

            <el-table
              :data="groupPageRows"
              size="small"
              :row-key="groupKeyOf"
              empty-text="曲库中没有曲目"
              @expand-change="onGroupExpand"
            >
              <el-table-column type="expand">
                <template #default="{ row }">
                  <div class="group-files">
                    <el-table :data="row.files" size="small">
                      <el-table-column label="类型" width="80">
                        <template #default="{ row: file }">
                          <el-tag size="small" :type="file.kind === 'artwork' ? 'warning' : 'success'">
                            {{ file.kind === 'artwork' ? '封面' : '音频' }}
                          </el-tag>
                        </template>
                      </el-table-column>
                      <el-table-column label="角色" width="90">
                        <template #default="{ row: file }">
                          {{ file.role === 'primary' ? '主资产' : file.role === 'variant' ? '转码' : '封面' }}
                        </template>
                      </el-table-column>
                      <el-table-column label="规格 / 说明" min-width="170" show-overflow-tooltip>
                        <template #default="{ row: file }">{{ file.detail || '—' }}</template>
                      </el-table-column>
                      <el-table-column label="大小" width="100" align="right">
                        <template #default="{ row: file }">{{ formatBytes(file.sizeBytes) || '—' }}</template>
                      </el-table-column>
                      <el-table-column label="磁盘" width="80">
                        <template #default="{ row: file }">
                          <el-tag v-if="file.onDisk" size="small" type="success">正常</el-tag>
                          <el-tag v-else size="small" type="danger">缺失</el-tag>
                        </template>
                      </el-table-column>
                      <el-table-column label="存储键" min-width="300" show-overflow-tooltip>
                        <template #default="{ row: file }"><span class="mono">{{ file.storageKey }}</span></template>
                      </el-table-column>
                      <el-table-column label="操作" width="90" fixed="right">
                        <template #default="{ row: file }">
                          <el-button
                            text
                            type="primary"
                            size="small"
                            :icon="View"
                            :disabled="!file.onDisk"
                            @click="openPreview(file)"
                          >
                            {{ file.kind === 'artwork' ? '查看' : '试听' }}
                          </el-button>
                        </template>
                      </el-table-column>
                    </el-table>
                  </div>
                </template>
              </el-table-column>

              <el-table-column label="封面" width="72">
                <template #default="{ row }">
                  <template v-if="coverFileOf(row)">
                    <img
                      v-if="coverThumbs[coverFileOf(row)!.storageKey]"
                      :src="coverThumbs[coverFileOf(row)!.storageKey]"
                      class="cover-thumb"
                      alt="封面"
                      @click="onCoverClick(row)"
                    />
                    <el-button v-else text :icon="Picture" class="cover-load" @click="onCoverClick(row)" />
                  </template>
                  <span v-else class="text-faint">—</span>
                </template>
              </el-table-column>
              <el-table-column label="曲目" min-width="220">
                <template #default="{ row }">
                  <div class="track-cell">
                    <strong>{{ row.title }}</strong>
                    <span class="text-muted">
                      {{ [row.artists, row.albumTitle].filter(Boolean).join(' · ') || '—' }}
                    </span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="时长" width="80" align="right">
                <template #default="{ row }">{{ row.trackId ? durationText(row.durationMs) : '—' }}</template>
              </el-table-column>
              <el-table-column label="状态" width="90">
                <template #default="{ row }">
                  <template v-if="row.trackId">
                    <el-tag v-if="row.isPublished" size="small" type="success">已发布</el-tag>
                    <el-tag v-else size="small" type="info">私有</el-tag>
                  </template>
                  <el-tag v-else size="small" type="info">杂项</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="文件" width="150" align="right">
                <template #default="{ row }">
                  {{ row.files.length }} 个 · {{ formatBytes(row.totalBytes) || '0 KB' }}
                </template>
              </el-table-column>
              <el-table-column label="健康" width="90">
                <template #default="{ row }">
                  <el-tag v-if="missingCountOf(row) > 0" size="small" type="danger">
                    缺失 {{ missingCountOf(row) }}
                  </el-tag>
                  <span v-else class="text-faint">完好</span>
                </template>
              </el-table-column>
            </el-table>
            <div class="pagination">
              <el-pagination
                v-model:current-page="groupPage"
                v-model:page-size="groupPageSize"
                :total="filteredGroups.length"
                :page-sizes="[100, 150, 200, 500, 1000]"
                layout="total, sizes, prev, pager, next"
                @size-change="groupPage = 1"
              />
            </div>
          </el-tab-pane>

          <el-tab-pane name="orphans">
            <template #label>
              孤儿文件
              <el-tag size="small" type="warning" style="margin-left: 6px">{{ filteredOrphans.length }}</el-tag>
            </template>

            <div v-if="orphanGroups.length" class="group-bar">
              <div v-for="group in orphanGroups" :key="group.dir" class="group-chip">
                <el-icon><Folder /></el-icon>
                <span class="mono">{{ group.dir }}/</span>
                <strong>{{ group.count }} 个</strong>
                <span class="text-muted">{{ formatBytes(group.bytes) || '0 KB' }}</span>
              </div>
            </div>

            <el-table :data="orphanPageRows" size="small" empty-text="没有孤儿文件">
              <el-table-column label="存储键" prop="storageKey" min-width="300" show-overflow-tooltip>
                <template #default="{ row }"><span class="mono">{{ row.storageKey }}</span></template>
              </el-table-column>
              <el-table-column label="类型" width="110">
                <template #default="{ row }">
                  <el-tag v-if="row.kind === 'artwork'" size="small" type="warning">图片 · {{ extOf(row.storageKey) }}</el-tag>
                  <el-tag v-else-if="row.kind === 'audio'" size="small" type="success">音频 · {{ extOf(row.storageKey) }}</el-tag>
                  <el-tag v-else size="small" type="info">{{ extOf(row.storageKey) || '—' }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="大小" width="110" align="right">
                <template #default="{ row }">{{ formatBytes(row.sizeBytes) || '—' }}</template>
              </el-table-column>
              <el-table-column label="操作" width="90">
                <template #default="{ row }">
                  <el-button
                    v-if="row.kind !== 'other'"
                    text
                    type="primary"
                    size="small"
                    :icon="View"
                    @click="openPreview(row)"
                  >
                    {{ row.kind === 'artwork' ? '查看' : '试听' }}
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
            <div class="pagination">
              <el-pagination
                v-model:current-page="orphanPage"
                v-model:page-size="listPageSize"
                :total="filteredOrphans.length"
                :page-sizes="[100, 150, 200, 500, 1000]"
                layout="total, sizes, prev, pager, next"
                @size-change="orphanPage = 1"
              />
            </div>
          </el-tab-pane>

          <el-tab-pane name="missing">
            <template #label>
              缺失文件
              <el-tag size="small" type="danger" style="margin-left: 6px">{{ filteredMissing.length }}</el-tag>
            </template>

            <el-table :data="missingPageRows" size="small" empty-text="没有缺失文件">
              <el-table-column label="存储键" min-width="300" show-overflow-tooltip>
                <template #default="{ row }"><span class="mono">{{ row.storageKey }}</span></template>
              </el-table-column>
              <el-table-column label="类型" width="110">
                <template #default="{ row }">
                  <el-tag v-if="row.kind === 'artwork'" size="small" type="warning">图片 · {{ extOf(row.storageKey) }}</el-tag>
                  <el-tag v-else-if="row.kind === 'audio'" size="small" type="success">音频 · {{ extOf(row.storageKey) }}</el-tag>
                  <el-tag v-else size="small" type="info">{{ extOf(row.storageKey) || '—' }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="归属引用" min-width="240" show-overflow-tooltip>
                <template #default="{ row }">{{ row.referencedBy.join('；') || '—' }}</template>
              </el-table-column>
            </el-table>
            <div class="pagination">
              <el-pagination
                v-model:current-page="missingPage"
                v-model:page-size="listPageSize"
                :total="filteredMissing.length"
                :page-sizes="[100, 150, 200, 500, 1000]"
                layout="total, sizes, prev, pager, next"
                @size-change="missingPage = 1"
              />
            </div>
            <p class="text-faint list-tip">缺失文件说明数据库仍有引用但磁盘已丢失，需人工核查或从备份恢复。</p>
          </el-tab-pane>

          <el-tab-pane name="dirs">
            <template #label>
              空目录
              <el-tag size="small" type="info" style="margin-left: 6px">{{ audit.emptyDirectoryCount }}</el-tag>
            </template>
            <div class="dir-list">
              <span v-for="dir in audit.emptyDirectories" :key="dir" class="dir mono">{{ dir }}</span>
              <span v-if="audit.emptyDirectories.length === 0" class="text-faint">没有空目录</span>
            </div>
          </el-tab-pane>
        </el-tabs>
      </div>
    </template>

    <el-dialog
      v-model="previewVisible"
      :title="previewFileName"
      width="640px"
      destroy-on-close
      @closed="onPreviewClosed"
    >
      <div v-loading="previewLoading" class="preview-body">
        <img v-if="previewKind === 'image' && previewUrl" :src="previewUrl" class="preview-image" alt="图片预览" />
        <div v-else-if="previewKind === 'audio' && previewUrl" class="preview-audio-wrap">
          <el-icon class="preview-audio-icon"><Headset /></el-icon>
          <audio :src="previewUrl" controls autoplay class="preview-audio" />
        </div>
      </div>
      <p class="mono text-faint preview-key">{{ previewKey }}</p>
    </el-dialog>
  </div>
</template>

<style scoped>
.heading-actions {
  display: flex;
  gap: 10px;
}

.metric-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 14px;
  min-height: 40px;
}

.metric {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 16px 18px;
  border: 1px solid var(--adm-border);
  border-radius: var(--adm-radius);
  background: #ffffff;
}

.metric.clickable {
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.metric.clickable:hover {
  border-color: var(--el-color-primary);
  box-shadow: 0 2px 8px rgb(64 128 255 / 12%);
}

.metric.active {
  border-color: var(--el-color-primary);
}

.metric.danger {
  border-color: #f3c9c2;
  background: #fdf5f4;
}

.metric.warning {
  border-color: #f0dcba;
  background: #fdf9f1;
}

.metric-label {
  color: var(--adm-text-2);
  font-size: 13px;
}

.metric-value {
  color: #111827;
  font-size: 24px;
  font-weight: 700;
}

.metric-hint {
  color: var(--adm-text-3);
  font-size: 12px;
}

.group-files {
  padding: 8px 16px 12px 48px;
  background: #fafbfc;
}

.cover-thumb {
  width: 44px;
  height: 44px;
  border-radius: 6px;
  object-fit: cover;
  cursor: zoom-in;
  display: block;
}

.cover-load {
  width: 44px;
  height: 44px;
  border: 1px dashed var(--adm-border);
  border-radius: 6px;
  font-size: 18px;
  color: var(--adm-text-3);
}

.track-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.track-cell .text-muted {
  font-size: 12px;
}

.group-bar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
}

.group-chip {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 1px solid #f0dcba;
  border-radius: 999px;
  background: #fdf9f1;
  font-size: 12px;
}

.list-tip {
  margin: 10px 0 0;
  font-size: 12px;
}

.dir-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.dir {
  padding: 4px 8px;
  border-radius: 5px;
  background: #f3f5f8;
  color: var(--adm-text-2);
}

.preview-body {
  min-height: 120px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.preview-image {
  max-width: 100%;
  max-height: 60vh;
  border-radius: 8px;
}

.preview-audio-wrap {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 14px;
  width: 100%;
}

.preview-audio-icon {
  font-size: 40px;
  color: var(--adm-text-3);
}

.preview-audio {
  width: 100%;
}

.preview-key {
  margin: 12px 0 0;
  font-size: 12px;
  word-break: break-all;
}
</style>
