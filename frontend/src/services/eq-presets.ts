/**
 * 音效（EQ）预设定义。
 *
 * 每个预设由一组 BiquadFilter 参数组成（type/frequency/gain/q），
 * 在 H5 端通过 Web Audio API 的 BiquadFilterNode 链应用到音频输出；
 * 非 H5 端（app）不支持 Web Audio，将降级为无音效（引擎层负责降级）。
 *
 * 说明：音频源通过 MediaElementSourceNode 接入，滤波器链为
 * 源 → filter1 → filter2 → … → destination。flat（原声）bands 为空 = 旁路。
 */

/** Web Audio BiquadFilter 节点类型 */
export type BiquadFilterType =
  | 'lowpass'
  | 'highpass'
  | 'bandpass'
  | 'lowshelf'
  | 'highshelf'
  | 'peaking'
  | 'notch'
  | 'allpass'

/** 单个滤波器的参数 */
export interface EqBand {
  type: BiquadFilterType
  /** 中心/截止频率（Hz） */
  frequency: number
  /** 增益（dB），仅 peaking/low shelf/high shelf 有效，其余忽略 */
  gain: number
  /** 品质因数 Q */
  q: number
}

/** 一个音效预设 */
export interface EqPreset {
  id: string
  bands: EqBand[]
}

/** 全部内置音效预设；首位 flat（原声）为默认/旁路 */
export const EQ_PRESETS: EqPreset[] = [
  { id: 'flat', bands: [] },
  {
    id: 'pop',
    bands: [
      { type: 'lowshelf', frequency: 60, gain: 3.5, q: 0.9 },
      { type: 'peaking', frequency: 400, gain: -1.5, q: 0.8 },
      { type: 'peaking', frequency: 8000, gain: 3.5, q: 0.8 },
      { type: 'highshelf', frequency: 10000, gain: 2, q: 0.7 },
    ],
  },
  {
    id: 'rock',
    bands: [
      { type: 'lowshelf', frequency: 90, gain: 5, q: 0.7 },
      { type: 'peaking', frequency: 350, gain: -2.5, q: 1.0 },
      { type: 'peaking', frequency: 3000, gain: 3.5, q: 1.0 },
      { type: 'highshelf', frequency: 8000, gain: 3, q: 0.7 },
    ],
  },
  {
    id: 'classical',
    bands: [
      { type: 'peaking', frequency: 200, gain: 2, q: 0.7 },
      { type: 'peaking', frequency: 2000, gain: -1, q: 1.2 },
      { type: 'peaking', frequency: 8000, gain: 2.5, q: 0.8 },
    ],
  },
  {
    id: 'jazz',
    bands: [
      { type: 'peaking', frequency: 150, gain: 2.5, q: 0.7 },
      { type: 'peaking', frequency: 1000, gain: 1, q: 0.9 },
      { type: 'peaking', frequency: 5000, gain: 2, q: 0.8 },
    ],
  },
  {
    id: 'vocal',
    bands: [
      { type: 'highpass', frequency: 80, gain: 0, q: 0.7 },
      { type: 'peaking', frequency: 1500, gain: 4, q: 1.0 },
      { type: 'peaking', frequency: 3000, gain: 3, q: 0.9 },
    ],
  },
  {
    id: 'bass',
    bands: [
      { type: 'lowshelf', frequency: 100, gain: 7, q: 0.7 },
      { type: 'peaking', frequency: 200, gain: 2, q: 0.8 },
      { type: 'peaking', frequency: 3000, gain: -1.5, q: 1.0 },
    ],
  },
  {
    id: 'treble',
    bands: [
      { type: 'highshelf', frequency: 6000, gain: 6, q: 0.7 },
      { type: 'peaking', frequency: 10000, gain: 3, q: 0.8 },
    ],
  },
]

/** 按 id 查找预设；未命中返回原声（flat） */
export function findEqPreset(id: string): EqPreset {
  return EQ_PRESETS.find((preset) => preset.id === id) ?? EQ_PRESETS[0]
}