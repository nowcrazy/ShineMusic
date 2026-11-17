<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { onLoad, onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Heart, Play } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import CoverArt from '@/components/CoverArt.vue'
import { getCollection } from '@/services/catalog'
import { resolveUrl } from '@/services/http'
import { playQueue } from '@/services/player'
import { isAuthenticated, requireLogin } from '@/services/session'
import { useCapabilities } from '@/services/capabilities'
import { loadSavedCollections, savedCollectionIds, savedCollectionsLoading, savedCollectionsError, savingCollectionIds, toggleSavedCollection } from '@/services/saved-collections'
import type { ApiCollectionDetail, Track } from '@/types/music'
const { t } = useI18n()
const capabilities = useCapabilities()
const id = ref('')
const detail = ref<ApiCollectionDetail | null>(null)
const tracks = ref<Track[]>([])
const loading = ref(true)
const loadError = ref(false)
const saved = computed(() => savedCollectionIds.value.has(id.value))
const saveMessage = ref('')
async function refreshSaved() { try { await loadSavedCollections(true) } catch { /* visible inline retry */ } }
watch(isAuthenticated, () => { saveMessage.value = ''; void refreshSaved() })
onShow(() => { void refreshSaved() })
onLoad(options => { id.value = String(options?.id ?? ''); void load() })
async function load() {
  loading.value = true
  loadError.value = false
  try {
    if (!id.value) throw new Error('Missing collection id')
    const result = await getCollection(id.value)
    detail.value = result.detail
    tracks.value = result.tracks
    uni.setNavigationBarTitle({ title: result.detail.title })
  } catch { loadError.value = true } finally { loading.value = false }
}
async function toggleSave() {
  if (!requireLogin() || !detail.value) return
  saveMessage.value = ''
  try {
    await toggleSavedCollection({ ...detail.value, trackCount: tracks.value.length })
    saveMessage.value = t(saved.value ? 'homePage.collectionSaved' : 'homePage.collectionUnsaved')
  } catch { saveMessage.value = t('homePage.operationFailed') }
}
</script>
<template>
  <view class="detail-page">
    <view v-if="loading" class="state-text" role="status">{{ t('catalogPage.loading') }}</view>
    <view v-else-if="loadError || !detail" class="state-text"><text>{{ t('catalogPage.collectionUnavailable') }}</text><button class="retry" @tap="load">{{ t('app.retry') }}</button></view>
    <template v-else>
      <view class="detail-header"><view class="detail-art"><CoverArt :src="detail.artworkUrl ? resolveUrl(detail.artworkUrl) : ''" :seed="detail.title" :rounded="8" :icon-size="44" /></view><view class="detail-copy"><text class="eyebrow">{{ t('catalogPage.editorialPicks') }}</text><text class="detail-title">{{ detail.title }}</text><text v-if="detail.description" class="detail-sub">{{ detail.description }}</text><text class="detail-meta">{{ t('catalog.trackCount', { count: tracks.length }) }}</text></view></view>
      <view class="detail-actions"><button class="play-all" :disabled="!tracks.length" @tap="playQueue(tracks)"><Play :size="16" fill="currentColor" />{{ t('catalogPage.playAll') }}</button><button v-if="capabilities.capabilities.editorialCollections" class="save-button" :class="{ saved }" :aria-pressed="saved" :disabled="savedCollectionsLoading || savingCollectionIds.size > 0" @tap="toggleSave"><Heart :size="16" :fill="saved ? 'currentColor' : 'none'" />{{ t(saved ? 'homePage.unsaveCollection' : 'homePage.saveCollection') }}</button></view>
      <view v-if="savedCollectionsLoading" class="state-text" role="status">{{ t('homePage.loading') }}</view>
      <button v-else-if="savedCollectionsError && isAuthenticated" class="retry" @tap="refreshSaved">{{ t('homePage.savedLoadFailed') }} · {{ t('app.retry') }}</button>
      <text v-if="saveMessage" class="save-message" role="status">{{ saveMessage }}</text>
      <view class="detail-list"><TrackRow v-for="(track, index) in tracks" :key="track.id" :track="track" :index="index" :queue="tracks" /><view v-if="!tracks.length" class="state-text">{{ t('catalogPage.collectionNoTracks') }}</view></view>
    </template>
  </view>
  <PlayerBar />
</template>
<style scoped lang="scss">
.detail-page { box-sizing: border-box; min-height: 100vh; padding: 28px 24px 160px; background: var(--sm-page-bg); color: var(--sm-text); }
.detail-header { display: flex; align-items: center; gap: 20px; }
.detail-art { width: 112px; height: 112px; flex: none; }
.detail-copy { min-width: 0; flex: 1; }
.eyebrow { display: block; font-size: 10px; letter-spacing: 1px; color: var(--sm-accent); }
.detail-title { display: block; margin-top: 8px; font-size: 24px; font-weight: 800; line-height: 1.3; overflow-wrap: anywhere; }
.detail-sub { display: block; margin-top: 8px; font-size: 12px; color: var(--sm-text-2); line-height: 1.6; overflow-wrap: anywhere; }
.detail-meta { display: block; margin-top: 8px; color: var(--sm-text-2); font-size: 11px; }
.detail-actions { display: flex; flex-wrap: wrap; gap: 12px; margin: 24px 0; }
button { margin: 0; padding: 12px 16px; line-height: 1.5; font-size: 12px; border-radius: 24px; }
button::after { border: 0; }
.play-all, .save-button { display: flex; align-items: center; gap: 8px; }
.play-all { background: var(--sm-accent); color: var(--sm-accent-ink); }
.save-button { background: var(--sm-surface); color: var(--sm-text-2); }
.save-button.saved, .retry { color: var(--sm-accent); background: var(--sm-accent-soft); }
.state-text { padding: 24px 0; color: var(--sm-text-2); font-size: 13px; }
.save-message { display: block; margin: 12px 0; color: var(--sm-text-2); font-size: 12px; }
@media(min-width:800px) { .detail-page { max-width: 940px; margin: auto; padding: 32px 32px 160px; } .detail-art { width: 180px; height: 180px; } .detail-title { font-size: 32px; } }
</style>
