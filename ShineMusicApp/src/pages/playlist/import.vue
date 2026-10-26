<script setup lang="ts">
/**
 * 歌单解析导入页：粘贴外部平台（网易云/QQ 音乐/酷我/YouTube Music）歌单链接，
 * 解析预览（标记已匹配曲库的曲目）→ 一键导入生成新歌单，未匹配曲目后台静默下载补齐。
 * 对应后端接口：services/playlist-import 的 parsePlaylistImport / importPlaylist。
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Link2, Loader, Music2 } from 'lucide-vue-next'
import { importPlaylist, parsePlaylistImport, type ApiPlaylistImportParse } from '@/services/playlist-import'

const { t } = useI18n()

// 页面状态：链接输入 / 新歌单名 / 解析中 / 导入中 / 预览结果
const url = ref('')
const name = ref('')
const parsing = ref(false)
const importing = ref(false)
const preview = ref<ApiPlaylistImportParse | null>(null)

const matchedCount = computed(() => preview.value?.tracks.filter((track) => track.matched).length ?? 0)

/** 解析歌单：成功后用原歌单名预填新歌单名 */
async function handleParse() {
  const link = url.value.trim()
  if (!link) {
    uni.showToast({ title: t('importPage.urlRequired'), icon: 'none' })
    return
  }
  if (parsing.value) return
  parsing.value = true
  preview.value = null
  try {
    preview.value = await parsePlaylistImport(link)
    if (!name.value.trim()) name.value = preview.value.name
  } catch (error) {
    uni.showToast({ title: error instanceof Error ? error.message : t('importPage.parseFailed'), icon: 'none' })
  } finally {
    parsing.value = false
  }
}

/** 导入为新歌单：成功后跳转到新歌单详情页 */
async function handleImport() {
  if (!preview.value || importing.value) return
  importing.value = true
  try {
    const result = await importPlaylist(url.value.trim(), name.value.trim() || undefined)
    uni.showToast({ title: t('importPage.success', { matched: result.matched, pending: result.pending }), icon: 'none', duration: 2500 })
    setTimeout(() => {
      uni.redirectTo({ url: `/pages/playlist/detail?id=${result.playlistId}` })
    }, 600)
  } catch (error) {
    uni.showToast({ title: error instanceof Error ? error.message : t('importPage.importFailed'), icon: 'none' })
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <scroll-view class="import-page" scroll-y :show-scrollbar="false">
    <view class="page-head">
      <text class="page-title">{{ t('importPage.title') }}</text>
      <text class="page-sub">{{ t('importPage.subtitle') }}</text>
    </view>

    <!-- 链接输入区 -->
    <view class="url-card">
      <view class="url-input-row">
        <Link2 :size="18" class="url-icon" />
        <input
          v-model="url"
          class="url-input"
          :placeholder="t('importPage.urlPlaceholder')"
          :disabled="parsing || importing"
          confirm-type="go"
          @confirm="handleParse"
        />
      </view>
      <view class="parse-button" :class="{ disabled: parsing }" @tap="handleParse">
        <Loader v-if="parsing" :size="16" class="spin" />
        <text>{{ parsing ? t('importPage.parsing') : t('importPage.parse') }}</text>
      </view>
    </view>

    <!-- 解析预览 -->
    <template v-if="preview">
      <view class="summary-card">
        <image v-if="preview.coverUrl" class="summary-cover" :src="preview.coverUrl" mode="aspectFill" />
        <view v-else class="summary-cover placeholder"><Music2 :size="24" /></view>
        <view class="summary-copy">
          <text class="summary-name">{{ preview.name }}</text>
          <text class="summary-meta">{{ t('importPage.summary', { total: preview.total, matched: matchedCount, pending: preview.total - matchedCount }) }}</text>
        </view>
      </view>

      <view class="name-row">
        <input v-model="name" class="name-input" :placeholder="t('importPage.namePlaceholder')" maxlength="100" />
        <view class="import-button" :class="{ disabled: importing }" @tap="handleImport">
          <Loader v-if="importing" :size="16" class="spin" />
          <text>{{ importing ? t('importPage.importing') : t('importPage.import') }}</text>
        </view>
      </view>

      <view class="track-list">
        <view v-for="(track, index) in preview.tracks" :key="index" class="track-row">
          <text class="track-index">{{ index + 1 }}</text>
          <view class="track-copy">
            <text class="track-title">{{ track.title }}</text>
            <text class="track-artist">{{ track.artists.join(' / ') || '-' }}</text>
          </view>
          <view class="track-status" :class="track.matched ? 'matched' : 'pending'">
            <Check v-if="track.matched" :size="13" />
            <text>{{ track.matched ? t('importPage.matched') : t('importPage.willDownload') }}</text>
          </view>
        </view>
      </view>
    </template>

    <view class="page-bottom-spacer"></view>
  </scroll-view>
</template>

<style lang="scss" scoped>
/* 歌单解析导入（沿用方向 B 设计令牌） */
.import-page { height: 100vh; padding: 32rpx 40rpx 0; box-sizing: border-box; background: var(--sm-page-bg); }
.page-head { display: flex; flex-direction: column; gap: 10rpx; padding: 12rpx 0 24rpx; }
.page-title { color: var(--sm-text); font-size: 36rpx; font-weight: 800; }
.page-sub { color: var(--sm-text-2); font-size: 19rpx; line-height: 1.6; }
.url-card { padding: 28rpx; border: 1rpx solid var(--sm-border); border-radius: 24rpx; background: var(--sm-surface-3); }
.url-input-row { display: flex; align-items: center; gap: 14rpx; }
.url-icon { flex: none; color: var(--sm-text-3); }
.url-input { flex: 1; min-width: 0; color: var(--sm-text); font-size: 22rpx; }
.parse-button { display: flex; align-items: center; justify-content: center; gap: 8rpx; margin-top: 22rpx; padding: 18rpx 0; border-radius: 44rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 22rpx; font-weight: 800; box-shadow: 0 10rpx 30rpx var(--sm-accent-glow); }
.parse-button.disabled { opacity: 0.6; }
.summary-card { display: flex; align-items: center; gap: 18rpx; margin-top: 32rpx; }
.summary-cover { width: 96rpx; height: 96rpx; border-radius: 16rpx; }
.summary-cover.placeholder { display: grid; place-items: center; background: var(--sm-cover-bg); color: var(--sm-cover-ink); }
.summary-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 8rpx; }
.summary-name { overflow: hidden; color: var(--sm-text); font-size: 24rpx; font-weight: 700; text-overflow: ellipsis; white-space: nowrap; }
.summary-meta { color: var(--sm-text-2); font-size: 18rpx; }
.name-row { display: flex; align-items: center; gap: 16rpx; margin-top: 24rpx; }
.name-input { flex: 1; min-width: 0; padding: 16rpx 22rpx; border: 1rpx solid var(--sm-border); border-radius: 16rpx; background: var(--sm-surface-3); color: var(--sm-text); font-size: 21rpx; }
.import-button { display: flex; flex: none; align-items: center; gap: 8rpx; padding: 16rpx 30rpx; border-radius: 40rpx; background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 20rpx; font-weight: 800; }
.import-button.disabled { opacity: 0.6; }
.track-list { margin-top: 28rpx; }
.track-row { display: flex; align-items: center; gap: 18rpx; padding: 18rpx 0; border-bottom: 1rpx solid var(--sm-border); }
.track-index { flex: none; width: 44rpx; color: var(--sm-text-3); font-size: 18rpx; text-align: center; }
.track-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 6rpx; }
.track-title { overflow: hidden; color: var(--sm-text); font-size: 21rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.track-artist { overflow: hidden; color: var(--sm-text-2); font-size: 17rpx; text-overflow: ellipsis; white-space: nowrap; }
.track-status { display: flex; flex: none; align-items: center; gap: 6rpx; font-size: 16rpx; }
.track-status.matched { color: var(--sm-accent); }
.track-status.pending { color: var(--sm-text-3); }
.spin { animation: sm-spin 1s linear infinite; }
@keyframes sm-spin { to { transform: rotate(360deg); } }
.page-bottom-spacer { height: 120rpx; }
</style>
