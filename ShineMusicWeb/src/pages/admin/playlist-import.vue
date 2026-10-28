<script setup lang="ts">
/**
 * 歌单解析导入：粘贴外部平台（网易云/QQ 音乐/酷我/酷狗/YouTube Music）歌单链接，
 * 解析预览（标记已匹配曲库的曲目）→ 一键导入生成新歌单，未匹配曲目后台静默下载补齐。
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Link, Download, MagicStick, Refresh, Search } from '@element-plus/icons-vue'
import {
  importPlaylist,
  parsePlaylistImport,
  PlaylistImportPlatformLabel,
  rematchPlaylist,
  type PlaylistImportParseResult,
  type PlaylistRematchResult,
} from '@/services/playlist-import'
import { getPlaylists } from '@/services/library'
import { describeError } from '@/services/errors'

const form = reactive({ url: '', name: '' })
const parsing = ref(false)
const importing = ref(false)
const preview = ref<PlaylistImportParseResult | null>(null)

const playlists = ref<{ id: string; name: string; trackCount: number }[]>([])
const rematchPlaylistId = ref('')
const rematching = ref(false)
const rematchResult = ref<PlaylistRematchResult | null>(null)

const matchedCount = computed(() => preview.value?.tracks.filter((track) => track.matched).length ?? 0)
const unmatchedCount = computed(() => (preview.value ? preview.value.total - matchedCount.value : 0))

/** 预览曲目关键词：本地匹配曲名 / 艺人 / 专辑（不必回源请求）。 */
const previewKeyword = ref('')
const previewTracks = computed(() => {
  const tracks = preview.value?.tracks ?? []
  const term = previewKeyword.value.trim().toLowerCase()
  if (!term) return tracks
  return tracks.filter(
    (track) =>
      track.title.toLowerCase().includes(term) ||
      (track.albumTitle ?? '').toLowerCase().includes(term) ||
      track.artists.some((artist) => artist.toLowerCase().includes(term)),
  )
})

function formatDuration(durationMs: number | null): string {
  if (!durationMs || durationMs <= 0) return '-'
  const totalSeconds = Math.round(durationMs / 1000)
  const minutes = Math.floor(totalSeconds / 60)
  const seconds = totalSeconds % 60
  return `${minutes}:${String(seconds).padStart(2, '0')}`
}

async function handleParse() {
  const url = form.url.trim()
  if (!url) {
    ElMessage.warning('请先粘贴歌单链接')
    return
  }
  parsing.value = true
  preview.value = null
  try {
    preview.value = await parsePlaylistImport(url)
    if (!form.name.trim()) form.name = preview.value.name
    ElMessage.success(`解析成功，共 ${preview.value.total} 首，已匹配曲库 ${matchedCount.value} 首`)
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    parsing.value = false
  }
}

async function handleImport() {
  if (!preview.value) return
  importing.value = true
  try {
    const result = await importPlaylist(form.url.trim(), form.name.trim() || undefined)
    await ElMessageBox.alert(
      `歌单已生成：共 ${result.total} 首，已匹配入库 ${result.matched} 首，待补齐 ${result.pending} 首。缺失曲目已在后台自动下载，完成后会自动变为可播放。`,
      '导入成功',
      { confirmButtonText: '知道了', type: 'success' },
    )
    preview.value = null
    form.url = ''
    form.name = ''
    rematchPlaylistId.value = result.playlistId
    await loadPlaylists()
  } catch (error) {
    ElMessage.error(describeError(error))
  } finally {
    importing.value = false
  }
}

/** 拉取当前用户歌单列表（补齐选择器数据源）。 */
async function loadPlaylists() {
  try {
    const page = await getPlaylists()
    playlists.value = page.items.map((item) => ({ id: item.id, name: item.name, trackCount: item.trackCount }))
  } catch (error) {
    ElMessage.error(describeError(error, '歌单列表加载失败'))
  }
}

/** 重新补齐选中歌单的缺失曲目：逐条重跑匹配（查曲库 → 搜索音源 → 重建下载任务）。 */
async function handleRematch() {
  if (!rematchPlaylistId.value) {
    ElMessage.warning('请先选择要补齐的歌单')
    return
  }
  rematching.value = true
  rematchResult.value = null
  try {
    rematchResult.value = await rematchPlaylist(rematchPlaylistId.value)
    ElMessage.success('补齐完成')
    await loadPlaylists()
  } catch (error) {
    ElMessage.error(describeError(error, '补齐失败'))
  } finally {
    rematching.value = false
  }
}

onMounted(loadPlaylists)
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <template #header>
        <div class="card-header">
          <span>歌单解析导入</span>
        </div>
      </template>

      <el-alert
        type="info"
        :closable="false"
        class="tip"
        title="支持平台：网易云音乐、QQ 音乐、酷我音乐、酷狗音乐、YouTube Music（YouTube Music 需服务端配置网络代理）。粘贴歌单链接后点击解析。"
      />

      <div class="url-row">
        <el-input
          v-model="form.url"
          placeholder="粘贴歌单链接，如 https://music.163.com/playlist?id=24381616"
          clearable
          size="large"
          @keyup.enter="handleParse"
        >
          <template #prefix>
            <el-icon><Link /></el-icon>
          </template>
        </el-input>
        <el-button type="primary" size="large" :loading="parsing" @click="handleParse">
          <el-icon v-if="!parsing"><Refresh /></el-icon>
          解析
        </el-button>
      </div>
    </el-card>

    <el-card v-if="preview" shadow="never" class="result-card">
      <template #header>
        <div class="card-header">
          <div class="playlist-info">
            <img v-if="preview.coverUrl" :src="preview.coverUrl" class="cover" alt="" />
            <div>
              <div class="playlist-name">
                {{ preview.name }}
                <el-tag size="small" effect="plain">{{ PlaylistImportPlatformLabel[preview.platform] ?? preview.platform }}</el-tag>
              </div>
              <div class="playlist-stats">
                共 {{ preview.total }} 首 · 已匹配曲库 {{ matchedCount }} 首 · 待补齐 {{ unmatchedCount }} 首
              </div>
            </div>
          </div>
          <div class="import-bar">
            <el-input
              v-model="previewKeyword"
              placeholder="搜索曲名 / 艺人 / 专辑"
              clearable
              class="preview-search"
            >
              <template #prefix><el-icon><Search /></el-icon></template>
            </el-input>
            <el-input v-model="form.name" placeholder="新歌单名称" class="name-input" maxlength="100" />
            <el-button type="primary" :loading="importing" @click="handleImport">
              <el-icon v-if="!importing"><Download /></el-icon>
              导入为新歌单
            </el-button>
          </div>
        </div>
      </template>

      <el-table :data="previewTracks" stripe max-height="560">
        <el-table-column type="index" label="#" width="60" />
        <el-table-column prop="title" label="标题" min-width="220" show-overflow-tooltip />
        <el-table-column label="艺人" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.artists.join(' / ') || '-' }}</template>
        </el-table-column>
        <el-table-column prop="albumTitle" label="专辑" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.albumTitle || '-' }}</template>
        </el-table-column>
        <el-table-column label="时长" width="90">
          <template #default="{ row }">{{ formatDuration(row.durationMs) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag v-if="row.matched" type="success" size="small">已匹配</el-tag>
            <el-tag v-else type="warning" size="small">待下载</el-tag>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card shadow="never" class="result-card">
      <template #header>
        <div class="card-header">
          <span>重新补齐缺失曲目</span>
        </div>
      </template>

      <el-alert
        type="info"
        :closable="false"
        class="tip"
        title="歌单里出现「待补齐 / 无音源」曲目，或下载任务丢失时使用：会逐条重跑匹配（先查曲库，再搜索音源），命中曲库的直接转正，其余重建下载任务。200+ 首约需 1~3 分钟，请勿关闭页面。"
      />

      <div class="rematch-row">
        <el-select v-model="rematchPlaylistId" placeholder="选择要补齐的歌单" filterable class="rematch-select">
          <el-option v-for="item in playlists" :key="item.id" :label="item.name" :value="item.id">
            <span>{{ item.name }}</span>
            <span class="option-count">{{ item.trackCount }} 首</span>
          </el-option>
        </el-select>
        <el-button type="primary" :loading="rematching" @click="handleRematch">
          <el-icon v-if="!rematching"><MagicStick /></el-icon>
          重新补齐
        </el-button>
      </div>

      <el-alert
        v-if="rematchResult"
        type="success"
        :closable="false"
        show-icon
        class="rematch-result"
        :title="`检查 ${rematchResult.total} 首：转正 ${rematchResult.promoted} 首 · 重建任务 ${rematchResult.retried} 首 · 仍无音源 ${rematchResult.noSource} 首 · 下载中保持 ${rematchResult.unchanged} 首`"
      />
    </el-card>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.tip {
  margin-bottom: 16px;
}

.url-row {
  display: flex;
  gap: 12px;
}

.rematch-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.rematch-select {
  width: 320px;
}

.option-count {
  float: right;
  margin-left: 16px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.rematch-result {
  margin-top: 12px;
}

.playlist-info {
  display: flex;
  align-items: center;
  gap: 12px;
}

.cover {
  width: 48px;
  height: 48px;
  border-radius: 6px;
  object-fit: cover;
}

.playlist-name {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 600;
}

.playlist-stats {
  margin-top: 4px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.import-bar {
  display: flex;
  gap: 12px;
}

.name-input {
  width: 260px;
}

.preview-search {
  width: 240px;
}
</style>
