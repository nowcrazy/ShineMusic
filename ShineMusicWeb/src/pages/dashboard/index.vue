<script setup lang="ts">
/**
 * 仪表盘：聚合曲库、导入、点歌、邀请与音源任务的待办数量，并提供快捷入口。
 */
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import {
  ChatDotRound,
  Download,
  Headset,
  Postcard,
  Refresh,
  UploadFilled,
} from '@element-plus/icons-vue'
import { getAdminTracks } from '@/services/admin-catalog'
import { listImportItems } from '@/services/admin-import'
import { getAdminSongRequests } from '@/services/admin-requests'
import { getInvitations } from '@/services/admin-invitation'
import { getDownloadTasks } from '@/services/admin-source-download'
import { describeError } from '@/services/errors'

const router = useRouter()
const loading = ref(false)
const stats = ref({
  tracks: 0,
  pendingReview: 0,
  pendingPublication: 0,
  openRequests: 0,
  invitations: 0,
  runningTasks: 0,
})

const cards = computed(() => [
  { key: 'tracks', label: '曲库曲目', value: stats.value.tracks, hint: '已发布及待整理曲目', icon: Headset, tone: 'green', to: '/catalog' },
  { key: 'pendingReview', label: '待审核导入', value: stats.value.pendingReview, hint: '等待人工审核的导入条目', icon: UploadFilled, tone: 'blue', to: '/import' },
  { key: 'pendingPublication', label: '待转公开', value: stats.value.pendingPublication, hint: '用户上传等待发布', icon: UploadFilled, tone: 'orange', to: '/import' },
  { key: 'openRequests', label: '待处理点歌', value: stats.value.openRequests, hint: '状态为待安排的需求', icon: ChatDotRound, tone: 'purple', to: '/requests' },
  { key: 'invitations', label: '邀请记录', value: stats.value.invitations, hint: '累计发出的邀请', icon: Postcard, tone: 'cyan', to: '/invitations' },
  { key: 'runningTasks', label: '音源任务', value: stats.value.runningTasks, hint: '排队 / 下载 / 待导入（不含已完成）', icon: Download, tone: 'slate', to: '/source-download' },
])

const shortcuts = [
  { title: '上传音频', desc: '批量导入并转码', to: '/import', icon: UploadFilled },
  { title: '审核发布', desc: '试听、修正元数据后入库', to: '/import', icon: Headset },
  { title: '处理点歌', desc: '推进或拒绝用户需求', to: '/requests', icon: ChatDotRound },
  { title: '清理存储', desc: '审计孤儿文件与空目录', to: '/storage', icon: Download },
]

async function loadStats() {
  loading.value = true
  const tasks = [
    getAdminTracks({ pageSize: 1 }),
    listImportItems('pending_review', 1, 1),
    listImportItems('pending_publication', 1, 1),
    getAdminSongRequests({ status: 'open', pageSize: 1 }),
    getInvitations(undefined, 1, 1),
    // 音源任务只统计「处理中」（排队/下载/待导入）；已完成与失败属历史，不计入待办。
    getDownloadTasks('queued', 1, 1),
    getDownloadTasks('downloading', 1, 1),
    getDownloadTasks('import_queued', 1, 1),
  ]
  const results = await Promise.allSettled(tasks)
  const total = (index: number) => {
    const result = results[index]
    return result.status === 'fulfilled' ? result.value.total : 0
  }
  stats.value = {
    tracks: total(0),
    pendingReview: total(1),
    pendingPublication: total(2),
    openRequests: total(3),
    invitations: total(4),
    runningTasks: total(5) + total(6) + total(7),
  }
  if (results.some((item) => item.status === 'rejected')) {
    const first = results.find((item) => item.status === 'rejected') as PromiseRejectedResult | undefined
    ElMessage.warning(describeError(first?.reason, '部分统计加载失败'))
  }
  loading.value = false
}

onMounted(loadStats)
</script>

<template>
  <div class="page">
    <div class="page-heading">
      <div>
        <h2>仪表盘</h2>
        <p>曲库与运营数据总览</p>
      </div>
      <el-button :icon="Refresh" :loading="loading" @click="loadStats">刷新数据</el-button>
    </div>

    <div class="stat-grid" v-loading="loading">
      <button
        v-for="card in cards"
        :key="card.key"
        class="stat-card"
        type="button"
        @click="router.push(card.to)"
      >
        <div class="stat-icon" :class="card.tone">
          <el-icon :size="20"><component :is="card.icon" /></el-icon>
        </div>
        <div class="stat-copy">
          <span class="stat-label">{{ card.label }}</span>
          <strong class="stat-value">{{ card.value }}</strong>
          <span class="stat-hint">{{ card.hint }}</span>
        </div>
      </button>
    </div>

    <div class="panel">
      <div class="panel-title">快捷入口</div>
      <div class="shortcut-grid">
        <button
          v-for="item in shortcuts"
          :key="item.title"
          class="shortcut"
          type="button"
          @click="router.push(item.to)"
        >
          <el-icon :size="18"><component :is="item.icon" /></el-icon>
          <div>
            <strong>{{ item.title }}</strong>
            <span>{{ item.desc }}</span>
          </div>
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.stat-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 14px;
}

.stat-card {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  padding: 18px;
  border: 1px solid var(--adm-border);
  border-radius: var(--adm-radius);
  background: #ffffff;
  text-align: left;
  cursor: pointer;
  transition: border-color 0.15s ease, box-shadow 0.15s ease, transform 0.15s ease;
}

.stat-card:hover {
  border-color: #bfe3d0;
  box-shadow: 0 6px 18px rgba(16, 24, 40, 0.06);
  transform: translateY(-1px);
}

.stat-icon {
  display: grid;
  flex: none;
  place-items: center;
  width: 42px;
  height: 42px;
  border-radius: 10px;
}

.stat-icon.green { background: #e9f8f0; color: #2f9e69; }
.stat-icon.blue { background: #e8f1fe; color: #3b76d9; }
.stat-icon.orange { background: #fdf1e3; color: #c47a1d; }
.stat-icon.purple { background: #f1ecfd; color: #7b58c9; }
.stat-icon.cyan { background: #e3f7f8; color: #1e8e94; }
.stat-icon.slate { background: #eef1f6; color: #5b6b82; }

.stat-copy {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.stat-label {
  color: var(--adm-text-2);
  font-size: 13px;
}

.stat-value {
  margin: 2px 0;
  color: #111827;
  font-size: 26px;
  font-weight: 700;
  line-height: 1.2;
}

.stat-hint {
  color: var(--adm-text-3);
  font-size: 12px;
}

.shortcut-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
}

.shortcut {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border: 1px solid var(--adm-border);
  border-radius: 9px;
  background: #fbfcfd;
  color: var(--adm-text);
  text-align: left;
  cursor: pointer;
  transition: background 0.15s ease, border-color 0.15s ease;
}

.shortcut:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--adm-primary-soft);
}

.shortcut > div {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.shortcut strong {
  font-size: 14px;
  font-weight: 600;
}

.shortcut span {
  color: var(--adm-text-2);
  font-size: 12px;
}
</style>
