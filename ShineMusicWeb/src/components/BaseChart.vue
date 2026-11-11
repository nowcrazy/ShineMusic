<script setup lang="ts">
/**
 * ECharts 轻量封装（按需引入饼图 / 条形图）。
 * 负责初始化、响应尺寸变化与销毁，并把点击事件透传给业务页。
 */
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { EChartsOption } from 'echarts'

echarts.use([BarChart, LineChart, PieChart, GridComponent, LegendComponent, TooltipComponent, CanvasRenderer])

/** 图表点击事件的最小信息集（业务页据此判断扇区 / 柱子含义）。 */
interface ChartClickParams {
  name: string
  seriesName: string
  dataIndex: number
  data?: unknown
}

const props = withDefaults(defineProps<{ option: EChartsOption; height?: string }>(), {
  height: '280px',
})

const emit = defineEmits<{ (event: 'chart-click', params: ChartClickParams): void }>()

type ChartInstance = ReturnType<typeof echarts.init>

const container = ref<HTMLDivElement | null>(null)
let chart: ChartInstance | null = null
let observer: ResizeObserver | null = null

function render() {
  chart?.setOption(props.option, true)
}

onMounted(() => {
  if (!container.value) return
  chart = echarts.init(container.value)
  chart.on('click', (params) => {
    emit('chart-click', {
      name: params.name ?? '',
      seriesName: params.seriesName ?? '',
      dataIndex: params.dataIndex,
      data: params.data,
    })
  })
  render()
  observer = new ResizeObserver(() => chart?.resize())
  observer.observe(container.value)
})

watch(() => props.option, render, { deep: true })

onBeforeUnmount(() => {
  observer?.disconnect()
  observer = null
  chart?.dispose()
  chart = null
})
</script>

<template>
  <div ref="container" class="base-chart" :style="{ height }" />
</template>

<style scoped>
.base-chart {
  width: 100%;
  min-height: 200px;
}
</style>
