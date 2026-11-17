<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight } from 'lucide-vue-next'
import CoverArt from './CoverArt.vue'
import { chartLabel, getCharts, type MusicChart } from '@/services/charts'
import { getGenres } from '@/services/catalog'
const { t } = useI18n()
const charts = ref<MusicChart[]>([])
const genres = ref<readonly string[]>([])
const genre = ref('')
const genresError = ref(false)
const genresLoading = ref(true)
const loading = ref(true)
const error = ref(false)
const lead = computed(() => charts.value.find(item => item.id === 'weekly-plays') ?? charts.value[0])
const others = computed(() => charts.value.filter(item => item.id !== lead.value?.id))
let sequence = 0
async function load() {
  const seq = ++sequence
  loading.value = true
  error.value = false
  try { const result = await getCharts(genre.value); if (seq === sequence) charts.value = result }
  catch { if (seq === sequence) error.value = true }
  finally { if (seq === sequence) loading.value = false }
}
async function loadGenres() {
  genresLoading.value = true
  genresError.value = false
  try { genres.value = await getGenres() } catch { genresError.value = true } finally { genresLoading.value = false }
}
function select(value: string) { if (genre.value === value) return; genre.value = value; void load() }
function open(id: string) { uni.navigateTo({ url: `/pages/charts/detail?id=${encodeURIComponent(id)}&genre=${encodeURIComponent(genre.value)}` }) }
onMounted(() => { void load(); void loadGenres() })
</script>

<template>
  <view class="charts">
    <scroll-view class="filters" scroll-x :show-scrollbar="false"><view class="filter-row"><button class="chip" :class="{ selected: !genre }" :aria-pressed="!genre" @tap="select('')">{{ t('home.genres') }}</button><button v-for="item in genres" :key="item" class="chip" :class="{ selected: genre === item }" :aria-pressed="genre === item" @tap="select(item)">{{ item }}</button></view></scroll-view>
    <text v-if="genresLoading" class="hint" role="status">{{ t('homePage.loading') }}</text>
    <button v-else-if="genresError" class="retry" @tap="loadGenres">{{ t('homePage.genreLoadFailed') }} · {{ t('app.retry') }}</button>
    <text v-else-if="!genres.length" class="hint">{{ t('homePage.genresEmpty') }}</text>
    <view v-if="loading" class="state" role="status">{{ t('homePage.loading') }}</view>
    <button v-else-if="error" class="retry" @tap="load">{{ t('app.error') }} · {{ t('app.retry') }}</button>
    <view v-else-if="!lead" class="state">{{ t('homePage.chartsEmpty') }}</view>
    <template v-else>
      <button class="chart-lead" @tap="open(lead.id)">
        <view class="chart-head"><view><text class="label">SHINE CHART / 01</text><text class="chart-title">{{ chartLabel(lead, t) }}</text></view><view class="round"><ChevronRight :size="18" /></view></view>
        <text class="hint">{{ chartLabel(lead, t, true) }}</text>
        <view v-for="(track, rank) in lead.tracks.slice(0, 3)" :key="track.id" class="preview-row"><text class="rank">{{ rank + 1 }}</text><view class="cover"><CoverArt :src="track.cover" :seed="track.title" :rounded="5" :icon-size="18" /></view><view class="track-copy"><text class="track-title">{{ track.title }}</text><text class="artist">{{ track.artist }}</text></view></view>
        <text v-if="!lead.tracks.length" class="state">{{ t('homePage.chartEmpty') }}</text>
      </button>
      <view class="chart-grid"><button v-for="(chart, index) in others" :key="chart.id" class="chart-tile" :class="`tone-${index % 3}`" :style="{ animationDelay: `${index * 40}ms` }" @tap="open(chart.id)"><text class="label">SHINE CHART / {{ String(index + 2).padStart(2, '0') }}</text><text class="tile-title">{{ chartLabel(chart, t) }}</text><text class="hint">{{ chartLabel(chart, t, true) }}</text><text class="tile-count">{{ chart.tracks.length ? t('homePage.trackCount', { count: chart.tracks.length }) : t('homePage.chartEmpty') }}</text><ChevronRight :size="16" /></button></view>
    </template>
  </view>
</template>
<style scoped lang="scss">
.charts { padding: 0 24px; color: var(--sm-text); }
button { margin: 0; line-height: 1.5; text-align: left; color: inherit; }
button::after { border: 0; }
button:focus-visible { outline: 2px solid var(--sm-accent); outline-offset: 3px; }
button:hover { opacity: .88; }
.filter-row { display: flex; gap: 8px; padding: 4px 0; }
.filters { margin-bottom: 20px; }
.chip { flex: none; padding: 8px 16px; border-radius: 24px; background: var(--sm-surface); color: var(--sm-text-2); font-size: 12px; }
.chip.selected { background: var(--sm-text); color: var(--sm-bg); }
.chart-lead { width: 100%; padding: 24px; background: var(--sm-muted-soft); border-radius: 12px; }
.chart-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.label { display: block; font-size: 10px; letter-spacing: 1.5px; color: var(--sm-text-2); }
.chart-title { display: block; margin-top: 8px; font-size: 24px; font-weight: 750; overflow-wrap: anywhere; }
.round { display: grid; place-items: center; width: 36px; height: 36px; flex: none; border: 1px solid var(--sm-border-strong); border-radius: 50%; }
.hint { display: block; margin-top: 8px; color: var(--sm-text-2); font-size: 11px; line-height: 1.7; }
.preview-row { display: grid; grid-template-columns: 20px 44px minmax(0,1fr); gap: 12px; align-items: center; padding: 16px 0; border-bottom: 1px solid var(--sm-border-strong); }
.preview-row:last-child { border: 0; padding-bottom: 0; }
.rank { color: var(--sm-accent-3); font: italic 25px Georgia, serif; }
.cover { width: 44px; height: 44px; }
.track-copy { min-width: 0; }
.track-title { display: block; font-size: 13px; font-weight: 600; overflow-wrap: anywhere; }
.artist { display: block; margin-top: 4px; font-size: 10px; color: var(--sm-text-2); overflow-wrap: anywhere; }
.chart-grid { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 12px; margin-top: 16px; }
.chart-tile { min-width: 0; width: 100%; padding: 24px 16px; border-radius: 10px; background: var(--sm-warn-soft); }
.tone-1 { background: var(--sm-info-soft); }
.tone-2 { background: var(--sm-accent-3-soft); }
.tile-title { display: block; margin-top: 12px; font-size: 21px; font-weight: 750; overflow-wrap: anywhere; }
.tile-count { display: block; margin: 16px 0 8px; font-size: 10px; color: var(--sm-text-2); }
.state { display: block; padding: 24px 0; color: var(--sm-text-2); font-size: 13px; }
.retry { margin: 16px 0; padding: 12px; background: var(--sm-surface); color: var(--sm-accent); font-size: 13px; }
@media(min-width:800px) { .charts { padding: 0 32px; } .chart-grid { grid-template-columns: repeat(3,minmax(0,1fr)); } }
@media(max-width:360px) { .charts { padding: 0 16px; } .chart-lead { padding: 20px; } }
@media(prefers-reduced-motion:no-preference) { .chart-tile { animation: arrive 260ms cubic-bezier(.2,.85,.25,1.05) both; } @keyframes arrive { from { opacity: .4; transform: translateY(6px); } to { opacity: 1; transform: none; } } }
</style>
