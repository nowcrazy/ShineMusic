<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { onLoad } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Play } from 'lucide-vue-next'
import TrackRow from '@/components/TrackRow.vue'
import PlayerBar from '@/components/PlayerBar.vue'
import { getCharts, chartLabel, type MusicChart } from '@/services/charts'
import { playQueue } from '@/services/player'
const { t } = useI18n()
const id = ref('')
const genre = ref('')
const chart = ref<MusicChart>()
const loading = ref(true)
const error = ref(false)
async function load() {
  loading.value = true
  error.value = false
  try { chart.value = (await getCharts(genre.value)).find(item => item.id === id.value) }
  catch { error.value = true }
  finally { loading.value = false }
}
function playAll() { if (chart.value?.tracks.length) playQueue(chart.value.tracks) }
onLoad(options => { id.value = String(options?.id ?? ''); genre.value = String(options?.genre ?? ''); void load() })
watchEffect(() => uni.setNavigationBarTitle({ title: chart.value ? chartLabel(chart.value, t) : t('homePage.chartsTitle') }))
</script>
<template>
  <view class="chart-page">
    <text class="eyebrow">{{ t('homePage.chartsTitle') }}</text>
    <view v-if="loading" class="state" role="status">{{ t('homePage.loading') }}</view>
    <button v-else-if="error" class="action" @tap="load">{{ t('app.error') }} · {{ t('app.retry') }}</button>
    <view v-else-if="!chart" class="state">{{ t('homePage.chartMissing') }}</view>
    <template v-else>
      <text class="title">{{ chartLabel(chart, t) }}</text>
      <text class="description">{{ chartLabel(chart, t, true) }}</text>
      <text v-if="genre" class="genre">{{ genre }}</text>
      <view class="list-heading"><text>{{ t('homePage.trackCount', { count: chart.tracks.length }) }}</text><button v-if="chart.tracks.length" class="action" @tap="playAll"><Play :size="18" /><text>{{ t('daily.play') }}</text></button></view>
      <view v-if="!chart.tracks.length" class="state">{{ t('homePage.chartEmpty') }}</view>
      <TrackRow v-for="(track, index) in chart.tracks" :key="track.id" :track="track" :index="index" :queue="chart.tracks" />
    </template>
  </view>
  <PlayerBar />
</template>
<style scoped lang="scss">
.chart-page { box-sizing: border-box; min-height: 100vh; max-width: 1120px; margin: auto; padding: 32px 24px 160px; color: var(--sm-text); background: var(--sm-page-bg); }
.eyebrow { display: block; color: var(--sm-accent); font-size: 14px; font-weight: 700; }
.title { display: block; margin-top: 16px; font-size: 32px; font-weight: 800; overflow-wrap: anywhere; }
.description { display: block; margin-top: 12px; color: var(--sm-text-2); line-height: 1.6; font-size: 14px; }
.list-heading { display: flex; flex-wrap: wrap; gap: 16px; align-items: center; justify-content: space-between; margin: 32px 0 16px; font-size: 14px; color: var(--sm-text-2); }
.action { display: flex; align-items: center; gap: 8px; margin: 16px 0; padding: 12px 20px; border-radius: var(--sm-radius-pill); background: var(--sm-accent-soft); color: var(--sm-accent); font-size: 14px; line-height: 1.5; }
.action::after { border: 0; }
.action:focus-visible { outline: 3px solid var(--sm-accent); outline-offset: 4px; }
.action:active { opacity: .75; }
.state { padding: 48px 0; font-size: 14px; color: var(--sm-text-2); }
.chart-page { max-width: 940px; }
.genre { display: inline-block; margin-top: 16px; padding: 8px 16px; border-radius: 24px; font-size: 12px; background: var(--sm-surface); color: var(--sm-text-2); }
.action { background: var(--sm-accent); color: var(--sm-accent-ink); font-size: 12px; }
.title { letter-spacing: -1px; }
</style>
