import type { ApiTrack, Track } from '@/types/music'
import { API_PREFIX, http } from './http'
import { mapTrack } from './catalog'

export interface ApiChart { id: string; title: string; description: string; tracks: ApiTrack[] }
export interface MusicChart extends Omit<ApiChart, 'tracks'> { tracks: Track[] }
/** Anonymous, real site statistics. Preserve server ordering and empty charts. */
export async function getCharts(genre?: string): Promise<MusicChart[]> {
  const result = await http.get<{ items: ApiChart[] }>(`${API_PREFIX}/charts${genre ? `?genre=${encodeURIComponent(genre)}` : ''}`, false)
  return result.items.map(chart => ({ ...chart, tracks: chart.tracks.map(mapTrack) }))
}
const keys: Record<string, [string, string]> = {
  'weekly-plays': ['homePage.weeklyTitle', 'homePage.weeklyHint'],
  favorites: ['homePage.favoritesTitle', 'homePage.favoritesHint'],
  'all-time-plays': ['homePage.allTimeTitle', 'homePage.allTimeHint'],
  'new-arrivals': ['homePage.newArrivalsTitle', 'homePage.newArrivalsHint'],
}
export function chartLabel(chart: MusicChart, t: (key: string) => string, description = false): string {
  const pair = keys[chart.id]
  return pair ? t(pair[description ? 1 : 0]) : description ? chart.description : chart.title
}
