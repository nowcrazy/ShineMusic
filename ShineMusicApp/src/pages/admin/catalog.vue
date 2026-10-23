<script setup lang="ts">
/**
 * 管理员曲库维护页：左侧搜索曲目列表，右侧编辑元数据（曲名、艺人、曲序/碟序、年份、风格）。
 * 进入参数：无。访问控制：onShow 校验 isAdmin，非管理员直接 redirectTo 首页。
 * 对应后端接口：services/admin-catalog 的 getAdminTracks（搜索/过滤，单页 100 条）、
 * updateAdminTrack（保存元数据修改）。
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { AlertCircle, Check, FileUp, ImageUp, Music, Plus, Search, ShieldCheck, Trash2 } from 'lucide-vue-next'
import { createTrackLyric, deleteLyric, getAdminTracks, getTrackLyrics, updateAdminTrack, updateLyric } from '@/services/admin-catalog'
import { resolveUrl } from '@/services/http'
import { describeError } from '@/services/errors'
import { uploadArtwork } from '@/services/admin-artwork'
import { isAdmin } from '@/services/session'
import type { ApiLyric, ApiTrack } from '@/types/music'

// —— 列表 / 选择 / 搜索过滤状态 ——
const tracks = ref<ApiTrack[]>([]) // 本地可变副本：后端契约是 readonly 数组，此处保存可原地更新的副本
const selectedId = ref('')
const query = ref('')
const missingMetadata = ref(false) // 是否只看缺少元数据的曲目
const loading = ref(false)
const saving = ref(false)
const loadError = ref('')
// —— 编辑表单字段（数字类以字符串承载，保存时再解析） ——
const title = ref('')
const artists = ref('')
const trackNumber = ref('')
const discNumber = ref('')
const releaseYear = ref('')
const genres = ref('')
// —— 封面管理 ——
const coverUploading = ref(false)
// 歌词文件导入：隐藏的 <input type="file">（仅 H5 平台可用，App/小程序请用文本输入）
const lyricFileInput = ref<HTMLInputElement | null>(null)

const { t } = useI18n()

// 当前选中的曲目对象（由 selectedId 在列表中派生）
const selectedTrack = computed(() => tracks.value.find((track) => track.id === selectedId.value) ?? null)

// 权限拦截：非管理员替换跳转首页（不可返回）；管理员进入即加载曲库
onShow(() => {
  if (!isAdmin.value) {
    uni.redirectTo({ url: '/pages/home/index' })
    return
  }
  void loadTracks()
})

/**
 * 按关键词与“缺少元数据”过滤条件加载曲目；加载后保持/修正当前选中项
 * 关键副作用：失败写入 loadError 供页面展示
 */
async function loadTracks() {
  loading.value = true
  loadError.value = ''
  try {
    const result = await getAdminTracks({
      q: query.value,
      missingMetadata: missingMetadata.value,
      pageSize: 100,
    })
    tracks.value = [...result.items]
    if (!tracks.value.some((track) => track.id === selectedId.value)) {
      selectTrack(tracks.value[0] ?? null)
    } else if (selectedTrack.value) {
      selectTrack(selectedTrack.value)
    }
  } catch (error) {
    // 展示具体原因（网络不可达 / 权限不足 / 服务端错误），避免统一文案掩盖真实故障
    loadError.value = describeError(error, t('catalogAdmin.loadFailed'))
  } finally {
    loading.value = false
  }
}

/**
 * 解析可选正整数输入：空串视为未填写（undefined），非法/非正数也按未填写处理
 * @param value 表单中的数字字符串
 */
function parseOptionalNumber(value: string) {
  const normalized = value.trim()
  if (!normalized) return undefined
  const parsed = Number(normalized)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : undefined
}

/**
 * 拆分多值文本：支持顿号/中英文逗号分隔，去空白、去重
 * @param value 表单中的艺人或风格字符串
 */
function splitValues(value: string) {
  return [...new Set(value.split(/[、,，]/).map((item) => item.trim()).filter(Boolean))]
}

/**
 * 保存当前曲目元数据：校验曲名/艺人必填，提交成功后就地替换列表项并重填表单
 * 关键副作用：saving 防重复提交；失败 toast 提示
 */
async function saveTrack() {
  const track = selectedTrack.value
  if (!track || saving.value) return
  // 表单校验：曲名与至少一位艺人均不能为空
  if (!title.value.trim() || !artists.value.trim()) {
    uni.showToast({ title: t('catalogAdmin.titleArtistRequired'), icon: 'none' })
    return
  }
  saving.value = true
  try {
    const updated = await updateAdminTrack(track.id, {
      title: title.value.trim(),
      artists: splitValues(artists.value),
      trackNumber: parseOptionalNumber(trackNumber.value),
      discNumber: parseOptionalNumber(discNumber.value),
      releaseYear: parseOptionalNumber(releaseYear.value),
      genres: splitValues(genres.value),
    })
    const index = tracks.value.findIndex((item) => item.id === updated.id)
    if (index >= 0) tracks.value.splice(index, 1, updated)
    selectTrack(updated)
    uni.showToast({ title: t('catalogAdmin.trackSaved'), icon: 'success' })
  } catch {
    uni.showToast({ title: t('catalogAdmin.saveFailed'), icon: 'none' })
  } finally {
    saving.value = false
  }
}

// ===== 封面管理 =====

/** 选择本地图片并上传为新封面，成功后关联当前曲目（PATCH artworkId） */
async function changeCover() {
  const track = selectedTrack.value
  if (!track || coverUploading.value) return
  uni.chooseImage({
    count: 1,
    sizeType: ['compressed', 'original'],
    success: async (res) => {
      const filePath = res.tempFilePaths[0]
      if (!filePath) return
      coverUploading.value = true
      try {
        const result = await uploadArtwork(filePath)
        // 后端 PATCH 会把曲目 artworkId 指向新封面，并返回带 artworkUrl 的曲目
        const updated = await updateAdminTrack(track.id, { artworkId: result.id })
        const index = tracks.value.findIndex((item) => item.id === updated.id)
        if (index >= 0) tracks.value.splice(index, 1, updated)
        uni.showToast({ title: t('catalogAdmin.coverUpdated'), icon: 'success' })
      } catch (error) {
        uni.showToast({ title: error instanceof Error ? error.message : t('catalogAdmin.coverUploadFailed'), icon: 'none' })
      } finally {
        coverUploading.value = false
      }
    },
    fail: () => {
      // 用户取消选择，静默处理
    },
  })
}

// ===== 歌词文件导入 =====

/** 触发隐藏的文件选择器（H5 可用） */
function pickLyricFile() {
  lyricFileInput.value?.click()
}

/** 读取用户选择的 .lrc/.txt 文件，填入歌词文本框并自动识别 LRC 格式 */
function onLyricFilePicked(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = () => {
    const text = String(reader.result ?? '')
    lyricForm.value.content = text
    // 含 LRC 时间轴标记则自动切到 LRC 格式
    if (/^\s*\[\d{1,2}:\d{2}(\.\d{1,3})?\]/m.test(text)) {
      lyricForm.value.format = 'lrc'
    }
    uni.showToast({ title: t('catalogAdmin.lyricsLoadedFromFile'), icon: 'none' })
  }
  reader.onerror = () => uni.showToast({ title: t('catalogAdmin.fileReadFailed'), icon: 'none' })
  reader.readAsText(file)
  input.value = '' // 允许重复选择同一文件
}

// ===== 歌词管理 =====
const lyrics = ref<ApiLyric[]>([])
const lyricsLoading = ref(false)
// 新增/编辑歌词表单
const lyricForm = ref({
  id: '' as string, // 非空表示编辑模式
  language: '',
  format: 'plain' as 'plain' | 'lrc',
  content: '',
  isDefault: false,
})
const lyricSaving = ref(false)
// 歌词内容输入框占位符：按格式（LRC/纯文本）拼接两行示例
const lyricPlaceholder = computed(() => {
  const [line1, line2] = lyricForm.value.format === 'lrc'
    ? ['catalogAdmin.lyricLrcPlaceholderLine1', 'catalogAdmin.lyricLrcPlaceholderLine2']
    : ['catalogAdmin.lyricPlainPlaceholderLine1', 'catalogAdmin.lyricPlainPlaceholderLine2']
  return `${t(line1)}\n${t(line2)}`
})

/** 选中曲目时加载其歌词列表 */
async function loadLyrics(trackId: string) {
  if (!trackId) { lyrics.value = []; return }
  lyricsLoading.value = true
  try {
    lyrics.value = await getTrackLyrics(trackId)
  } catch {
    lyrics.value = []
  } finally {
    lyricsLoading.value = false
  }
}

/** 选中曲目时同时加载歌词 */
function selectTrack(track: ApiTrack | null) {
  selectedId.value = track?.id ?? ''
  title.value = track?.title ?? ''
  artists.value = track?.artists.map((artist) => artist.name).join(t('catalogAdmin.valueSeparator')) ?? ''
  trackNumber.value = track?.trackNumber?.toString() ?? ''
  discNumber.value = track?.discNumber?.toString() ?? ''
  releaseYear.value = track?.releaseYear?.toString() ?? ''
  genres.value = track?.genres.join(t('catalogAdmin.valueSeparator')) ?? ''
  resetLyricForm()
  void loadLyrics(track?.id ?? '')
}

/** 重置歌词表单为新增模式 */
function resetLyricForm() {
  lyricForm.value = { id: '', language: '', format: 'plain', content: '', isDefault: false }
}

/** 把已有歌词填入表单进入编辑模式 */
function editLyric(lyric: ApiLyric) {
  lyricForm.value = {
    id: lyric.id,
    language: lyric.language ?? '',
    format: lyric.format,
    content: lyric.content,
    isDefault: lyric.isDefault,
  }
  uni.pageScrollTo({ selector: '.lyric-form', duration: 200 })
}

/** 保存歌词：新增或更新 */
async function saveLyric() {
  const track = selectedTrack.value
  if (!track || lyricSaving.value) return
  if (!lyricForm.value.content.trim()) {
    uni.showToast({ title: t('catalogAdmin.lyricsEmpty'), icon: 'none' })
    return
  }
  lyricSaving.value = true
  try {
    if (lyricForm.value.id) {
      // 更新模式
      await updateLyric(lyricForm.value.id, {
        language: lyricForm.value.language || null,
        format: lyricForm.value.format,
        content: lyricForm.value.content,
        isDefault: lyricForm.value.isDefault,
      })
      uni.showToast({ title: t('catalogAdmin.lyricUpdated'), icon: 'success' })
    } else {
      // 新增模式
      await createTrackLyric(track.id, {
        language: lyricForm.value.language || null,
        format: lyricForm.value.format,
        content: lyricForm.value.content,
        isDefault: lyricForm.value.isDefault,
      })
      uni.showToast({ title: t('catalogAdmin.lyricAdded'), icon: 'success' })
    }
    resetLyricForm()
    await loadLyrics(track.id)
  } catch {
    uni.showToast({ title: t('catalogAdmin.saveFailed'), icon: 'none' })
  } finally {
    lyricSaving.value = false
  }
}

/** 删除歌词 */
async function removeLyric(lyric: ApiLyric) {
  uni.showModal({
    title: t('catalogAdmin.deleteLyricTitle'),
    content: t('catalogAdmin.deleteLyricConfirm'),
    confirmText: t('admin.common.delete'),
    confirmColor: '#d0553a',
    success: async ({ confirm }) => {
      if (!confirm) return
      try {
        await deleteLyric(lyric.id)
        if (lyricForm.value.id === lyric.id) resetLyricForm()
        await loadLyrics(selectedTrack.value?.id ?? '')
        uni.showToast({ title: t('catalogAdmin.deleted'), icon: 'success' })
      } catch {
        uni.showToast({ title: t('catalogAdmin.deleteFailed'), icon: 'none' })
      }
    },
  })
}
</script>

<template>
  <scroll-view class="admin-page" scroll-y :show-scrollbar="false">
    <!-- 页头：管理员标识与说明 -->
    <view class="page-heading">
      <view class="heading-icon"><ShieldCheck :size="20" /></view>
      <view class="heading-copy">
        <text class="heading-title">{{ $t('admin.catalog.title') }}</text>
        <text class="heading-subtitle">{{ $t('catalogAdmin.subtitle') }}</text>
      </view>
    </view>

    <!-- 搜索栏：回车或点“搜索”触发查询 -->
    <view class="search-row">
      <view class="search-field">
        <Search :size="18" />
        <input v-model="query" confirm-type="search" :placeholder="$t('catalogAdmin.searchPlaceholder')" @confirm="loadTracks" />
      </view>
      <view class="search-action" @tap="loadTracks">{{ $t('admin.common.search') }}</view>
    </view>
    <!-- 过滤开关：切换后立即重新加载“仅缺元数据”结果 -->
    <view class="filter-row" @tap="missingMetadata = !missingMetadata; loadTracks()">
      <view class="filter-check" :class="{ checked: missingMetadata }"><Check v-if="missingMetadata" :size="13" /></view>
      <text>{{ $t('catalogAdmin.missingOnly') }}</text>
    </view>

    <view v-if="loading" class="state-message">{{ $t('catalogAdmin.loading') }}</view>
    <view v-else-if="loadError" class="state-message error"><AlertCircle :size="17" /><text>{{ loadError }}</text></view>
    <view v-else-if="!tracks.length" class="state-message">{{ $t('catalogAdmin.noTracksFound') }}</view>

    <!-- 工作区：左曲目列表 / 右元数据编辑器 -->
    <view v-else class="workspace">
      <!-- 曲目搜索结果列表：点击行选中并填充编辑表单 -->
      <view class="track-list">
        <view
          v-for="track in tracks"
          :key="track.id"
          class="track-item"
          :class="{ active: selectedId === track.id }"
          @tap="selectTrack(track)"
        >
          <view class="track-copy">
            <text class="track-title">{{ track.title }}</text>
            <text class="track-meta">{{ track.artists.map((artist) => artist.name).join($t('catalogAdmin.valueSeparator')) || $t('catalogAdmin.missingArtist') }}<text v-if="track.album"> · {{ track.album.title }}</text></text>
          </view>
          <text class="track-length">{{ track.durationMs ? $t('catalogAdmin.durationMinutes', { count: Math.round(track.durationMs / 60000) }) : $t('catalogAdmin.durationUnknown') }}</text>
        </view>
      </view>

      <!-- 元数据编辑面板：选中曲目后出现 -->
      <view v-if="selectedTrack" class="editor">
        <view class="editor-heading">
          <text class="editor-title">{{ $t('catalogAdmin.editTrackInfo') }}</text>
          <text class="editor-id">{{ selectedTrack.id }}</text>
        </view>

        <!-- 封面：展示当前封面并提供更换入口 -->
        <view class="cover-block">
          <text class="form-label">{{ $t('catalogAdmin.coverLabel') }}</text>
          <view class="cover-row">
            <view class="cover-thumb">
              <image v-if="selectedTrack.artworkUrl" class="cover-thumb-img" :src="resolveUrl(selectedTrack.artworkUrl)" mode="aspectFill" />
              <Music v-else :size="22" />
            </view>
            <view class="cover-ops">
              <view class="cover-button" :class="{ disabled: coverUploading }" @tap="changeCover">
                <ImageUp v-if="!coverUploading" :size="16" /><text>{{ coverUploading ? $t('catalogAdmin.uploading') : $t('catalogAdmin.changeCover') }}</text>
              </view>
              <text class="cover-hint">{{ $t('catalogAdmin.coverHint') }}</text>
            </view>
          </view>
        </view>
        <label class="form-field"><text>{{ $t('catalogAdmin.fieldTitle') }}</text><input v-model="title" maxlength="300" /></label>
        <label class="form-field"><text>{{ $t('admin.catalog.artist') }}</text><input v-model="artists" :placeholder="$t('catalogAdmin.artistsPlaceholder')" maxlength="2000" /></label>
        <view class="number-grid">
          <label class="form-field"><text>{{ $t('catalogAdmin.trackNumberLabel') }}</text><input v-model="trackNumber" type="number" /></label>
          <label class="form-field"><text>{{ $t('catalogAdmin.discNumberLabel') }}</text><input v-model="discNumber" type="number" /></label>
          <label class="form-field"><text>{{ $t('catalogAdmin.yearLabel') }}</text><input v-model="releaseYear" type="number" /></label>
        </view>
        <label class="form-field"><text>{{ $t('admin.catalog.genre') }}</text><input v-model="genres" :placeholder="$t('catalogAdmin.genresPlaceholder')" maxlength="3000" /></label>
        <view class="save-button" :class="{ disabled: saving }" @tap="saveTrack">
          <Check :size="17" /><text>{{ saving ? $t('catalogAdmin.saving') : $t('catalogAdmin.saveChanges') }}</text>
        </view>
      </view>

      <!-- 歌词管理面板：为已发布曲目增删改歌词 -->
      <view v-if="selectedTrack" class="lyric-panel">
        <view class="lyric-heading">
          <text class="lyric-title">{{ $t('catalogAdmin.lyricsTitle') }}</text>
          <text class="lyric-subtitle">{{ $t('catalogAdmin.lyricsSubtitle') }}</text>
        </view>

        <!-- 新增/编辑歌词表单 -->
        <view class="lyric-form">
          <view class="form-row">
            <label class="form-field small">
              <text>{{ $t('catalogAdmin.languageLabel') }}</text>
              <input v-model="lyricForm.language" :placeholder="$t('catalogAdmin.languagePlaceholder')" maxlength="32" />
            </label>
            <label class="form-field small">
              <text>{{ $t('catalogAdmin.formatLabel') }}</text>
              <view class="format-switch">
                <view :class="{ active: lyricForm.format === 'plain' }" @tap="lyricForm.format = 'plain'">{{ $t('catalogAdmin.formatPlain') }}</view>
                <view :class="{ active: lyricForm.format === 'lrc' }" @tap="lyricForm.format = 'lrc'">LRC</view>
              </view>
            </label>
            <label class="form-field checkbox-field" @tap="lyricForm.isDefault = !lyricForm.isDefault">
              <view class="filter-check" :class="{ checked: lyricForm.isDefault }"><Check v-if="lyricForm.isDefault" :size="13" /></view>
              <text>{{ $t('catalogAdmin.setDefaultLyric') }}</text>
            </label>
          </view>
          <label class="form-field">
            <text>{{ $t('catalogAdmin.lyricsContentLabel') }} {{ lyricForm.format === 'lrc' ? $t('catalogAdmin.lyricsContentLrcHint') : $t('catalogAdmin.lyricsContentPlainHint') }}</text>
            <textarea v-model="lyricForm.content" class="lyric-textarea" :placeholder="lyricPlaceholder" />
          </label>
          <view class="lyric-file-row">
            <view class="lyric-file-button" @tap="pickLyricFile"><FileUp :size="16" /><text>{{ $t('catalogAdmin.importFromFile') }}</text></view>
            <text class="lyric-file-hint">{{ $t('catalogAdmin.lyricFileHint') }}</text>
            <input ref="lyricFileInput" class="lyric-file-input" type="file" accept=".lrc,.txt" @change="onLyricFilePicked" />
          </view>
          <view class="lyric-actions">
            <view v-if="lyricForm.id" class="cancel-button" @tap="resetLyricForm">{{ $t('catalogAdmin.cancelEdit') }}</view>
            <view class="save-button" :class="{ disabled: lyricSaving }" @tap="saveLyric">
              <Plus :size="17" /><text>{{ lyricSaving ? $t('catalogAdmin.saving') : lyricForm.id ? $t('catalogAdmin.updateLyric') : $t('catalogAdmin.addLyric') }}</text>
            </view>
          </view>
        </view>

        <!-- 已有歌词列表 -->
        <view class="lyric-list">
          <text class="lyric-list-title">{{ $t('catalogAdmin.existingLyrics', { count: lyrics.length }) }}</text>
          <view v-if="lyricsLoading" class="state-message">{{ $t('catalogAdmin.loadingLyrics') }}</view>
          <view v-else-if="!lyrics.length" class="lyric-empty">{{ $t('catalogAdmin.noLyricsYet') }}</view>
          <view v-for="lyric in lyrics" :key="lyric.id" class="lyric-item">
            <view class="lyric-item-head">
              <view class="lyric-tags">
                <text class="tag lang">{{ lyric.language || $t('catalogAdmin.unspecifiedLanguage') }}</text>
                <text class="tag" :class="lyric.format">{{ lyric.format === 'lrc' ? 'LRC' : $t('catalogAdmin.formatPlain') }}</text>
                <text v-if="lyric.isDefault" class="tag default">{{ $t('catalogAdmin.defaultTag') }}</text>
              </view>
              <view class="lyric-item-ops">
                <view class="op-btn" @tap="editLyric(lyric)"><Check :size="13" />{{ $t('admin.common.edit') }}</view>
                <view class="op-btn danger" @tap="removeLyric(lyric)"><Trash2 :size="13" />{{ $t('admin.common.delete') }}</view>
              </view>
            </view>
            <text class="lyric-preview">{{ lyric.content.slice(0, 200) }}{{ lyric.content.length > 200 ? '…' : '' }}</text>
          </view>
        </view>
      </view>
    </view>
    <view class="bottom-space"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 曲库维护（方向 B · 玻璃质感）：深色底 + 彩色光晕 + 毛玻璃卡片 */
.admin-page { height: 100vh; padding: 30rpx 34rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.page-heading { display: flex; align-items: center; gap: 16rpx; padding: 10rpx 0 28rpx; }
.heading-icon { display: grid; place-items: center; width: 68rpx; height: 68rpx; border-radius: 18rpx; background: var(--sm-accent-soft); border: 1rpx solid var(--sm-border); color: var(--sm-accent); }
.heading-copy { display: flex; flex-direction: column; gap: 6rpx; }
.heading-title { color: var(--sm-text); font-size: 29rpx; font-weight: 800; }
.heading-subtitle { color: var(--sm-text-2); font-size: 17rpx; }
.search-row { display: flex; align-items: center; gap: 12rpx; }
.search-field { display: flex; flex: 1; align-items: center; gap: 12rpx; min-width: 0; height: 78rpx; padding: 0 18rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface); color: var(--sm-text-2); -webkit-backdrop-filter: blur(14px); backdrop-filter: blur(14px); }
.search-field input { flex: 1; min-width: 0; color: var(--sm-text); font-size: 19rpx; }
.search-field input::placeholder { color: var(--sm-text-3); }
.search-action { display: grid; place-items: center; width: 88rpx; height: 78rpx; border-radius: 16rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 700; }
.filter-row { display: flex; align-items: center; gap: 10rpx; padding: 18rpx 2rpx 22rpx; color: var(--sm-text-2); font-size: 17rpx; }
.filter-check { display: grid; place-items: center; width: 28rpx; height: 28rpx; border: 1rpx solid var(--sm-border-strong); border-radius: 7rpx; background: var(--sm-surface-2); color: var(--sm-accent-ink); }
.filter-check.checked { border-color: transparent; background: var(--sm-accent); }
.state-message { display: flex; align-items: center; justify-content: center; gap: 8rpx; min-height: 140rpx; color: var(--sm-text-2); font-size: 18rpx; }
.state-message.error { color: var(--sm-danger); }
.workspace { display: flex; flex-direction: column; gap: 22rpx; }
.track-list { overflow: hidden; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.track-item { display: flex; align-items: center; gap: 10rpx; min-height: 86rpx; padding: 14rpx 18rpx; border-bottom: 1rpx solid var(--sm-divider); }
.track-item:last-child { border-bottom: 0; }
.track-item.active { background: var(--sm-accent-soft); }
.track-copy { display: flex; flex: 1; flex-direction: column; gap: 6rpx; min-width: 0; }
.track-title { overflow: hidden; color: var(--sm-text); font-size: 19rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.track-meta { overflow: hidden; color: var(--sm-text-2); font-size: 15rpx; text-overflow: ellipsis; white-space: nowrap; }
.track-length { flex: none; color: var(--sm-text-3); font-size: 14rpx; }
.editor { padding: 22rpx; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.editor-heading { display: flex; flex-direction: column; gap: 6rpx; padding-bottom: 14rpx; }
.editor-title { color: var(--sm-text); font-size: 20rpx; font-weight: 800; }
.editor-id { overflow: hidden; color: var(--sm-text-3); font-family: monospace; font-size: 12rpx; text-overflow: ellipsis; white-space: nowrap; }
.form-field { display: flex; flex-direction: column; gap: 8rpx; margin-top: 15rpx; color: var(--sm-text-2); font-size: 15rpx; }
.form-label { display: block; margin-top: 18rpx; margin-bottom: 10rpx; color: var(--sm-text-2); font-size: 15rpx; font-weight: 700; }

/* ===== 封面管理 ===== */
.cover-block { margin-top: 6rpx; }
.cover-row { display: flex; align-items: center; gap: 18rpx; }
.cover-thumb { display: grid; flex: none; place-items: center; width: 130rpx; height: 130rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-cover-bg); color: var(--sm-cover-ink); overflow: hidden; }
.cover-thumb-img { width: 100%; height: 100%; }
.cover-ops { display: flex; flex: 1; flex-direction: column; gap: 10rpx; min-width: 0; }
.cover-button { display: flex; align-items: center; justify-content: center; gap: 8rpx; height: 72rpx; border-radius: 12rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 17rpx; font-weight: 700; }
.cover-button.disabled { opacity: .55; }
.cover-hint { color: var(--sm-text-3); font-size: 14rpx; }
.form-field input { height: 72rpx; padding: 0 14rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 10rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 17rpx; }
.form-field input::placeholder { color: var(--sm-text-3); }
.number-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12rpx; }
.save-button { display: flex; align-items: center; justify-content: center; gap: 8rpx; height: 78rpx; margin-top: 22rpx; border-radius: 12rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 18rpx; font-weight: 800; }
.save-button.disabled { opacity: .55; }
.bottom-space { height: 50rpx; }

/* ===== 歌词管理 ===== */
.lyric-panel { padding: 22rpx; border: 1rpx solid var(--sm-border); border-radius: 14rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.lyric-heading { display: flex; flex-direction: column; gap: 6rpx; padding-bottom: 14rpx; border-bottom: 1rpx solid var(--sm-divider); }
.lyric-title { color: var(--sm-text); font-size: 20rpx; font-weight: 800; }
.lyric-subtitle { color: var(--sm-text-2); font-size: 14rpx; }
.lyric-form { padding-top: 16rpx; }
.form-row { display: flex; gap: 12rpx; flex-wrap: wrap; }
.form-field.small { flex: 1; min-width: 180rpx; }
.form-field.checkbox-field { flex-direction: row; align-items: center; gap: 8rpx; justify-content: center; padding-top: 30rpx; }
.format-switch { display: flex; height: 72rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 10rpx; overflow: hidden; background: var(--sm-surface-2); }
.format-switch view { flex: 1; display: grid; place-items: center; font-size: 15rpx; color: var(--sm-text-2); }
.format-switch view.active { background: var(--sm-accent); color: var(--sm-accent-ink); font-weight: 700; }
.lyric-textarea { min-height: 260rpx; padding: 12rpx 14rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 10rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 15rpx; line-height: 1.6; resize: vertical; }
.lyric-actions { display: flex; gap: 12rpx; margin-top: 16rpx; }
.lyric-file-row { display: flex; align-items: center; gap: 12rpx; margin-top: 14rpx; }
.lyric-file-button { display: flex; flex: none; align-items: center; gap: 8rpx; height: 64rpx; padding: 0 22rpx; border: 1rpx solid var(--sm-border); border-radius: 12rpx; background: var(--sm-surface-2); color: var(--sm-text); font-size: 16rpx; font-weight: 600; }
.lyric-file-hint { color: var(--sm-text-3); font-size: 13rpx; }
.lyric-file-input { display: none; }
.lyric-actions .save-button { flex: 1; margin-top: 0; }
.cancel-button { display: grid; place-items: center; width: 160rpx; height: 78rpx; border-radius: 12rpx; border: 1rpx solid var(--sm-border); background: var(--sm-divider); color: var(--sm-text-2); font-size: 16rpx; font-weight: 600; }
.lyric-list { margin-top: 20rpx; }
.lyric-list-title { color: var(--sm-text-2); font-size: 15rpx; font-weight: 700; }
.lyric-empty { padding: 20rpx; color: var(--sm-text-3); font-size: 15rpx; text-align: center; }
.lyric-item { margin-top: 14rpx; padding: 16rpx; border: 1rpx solid var(--sm-surface-3); border-radius: 12rpx; background: var(--sm-surface-2); }
.lyric-item-head { display: flex; align-items: center; justify-content: space-between; gap: 10rpx; margin-bottom: 10rpx; }
.lyric-tags { display: flex; gap: 8rpx; flex-wrap: wrap; }
.tag { padding: 4rpx 12rpx; border-radius: 8rpx; font-size: 13rpx; font-weight: 600; background: var(--sm-surface-3); color: var(--sm-text-2); }
.tag.lang { background: var(--sm-info-soft); color: var(--sm-accent-2); }
.tag.lrc { background: var(--sm-warn-soft); color: var(--sm-warn); }
.tag.plain { background: var(--sm-accent-soft); color: var(--sm-accent); }
.tag.default { background: var(--sm-accent); color: var(--sm-accent-ink); }
.lyric-item-ops { display: flex; gap: 10rpx; flex-shrink: 0; }
.op-btn { display: flex; align-items: center; gap: 4rpx; padding: 6rpx 14rpx; border-radius: 8rpx; background: var(--sm-surface-3); color: var(--sm-text-2); font-size: 14rpx; }
.op-btn.danger { background: var(--sm-danger-soft); color: var(--sm-danger); }
.op-btn:active { opacity: .7; }
.lyric-preview { display: -webkit-box; overflow: hidden; color: var(--sm-text-2); font-size: 14rpx; line-height: 1.6; -webkit-line-clamp: 5; -webkit-box-orient: vertical; white-space: pre-wrap; }
</style>