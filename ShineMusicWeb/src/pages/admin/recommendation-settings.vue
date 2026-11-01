<script setup lang="ts">
/**
 * 个性化推荐策略配置：更新值次日生效，不改变当天已生成的用户推荐。
 */
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh } from '@element-plus/icons-vue'
import {
  getRecommendationStrategy,
  updateRecommendationStrategy,
} from '@/services/admin-recommendation'
import { describeError } from '@/services/errors'
import type {
  ApiRecommendationStrategyAdmin,
  ApiRecommendationStrategyValues,
} from '@/types/api.generated'

const loading = ref(false)
const saving = ref(false)
const strategy = ref<ApiRecommendationStrategyAdmin | null>(null)
const loadError = ref('')
const form = reactive<ApiRecommendationStrategyValues>({
  dailyTrackCount: 30,
  artistAffinityWeight: 2.5,
  playCountWeight: 3,
  playCountCap: 20,
  skipPenalty: 4,
  favoriteBoost: 6,
  likeBoost: 8,
  dislikePenalty: 40,
  nonRepeatDays: 7,
  recentRecommendationPenalty: 25,
  newTrackBoostDays: 90,
  newTrackBoostMax: 3,
})

function fillForm(values: ApiRecommendationStrategyValues) {
  Object.assign(form, values)
}

async function load() {
  loading.value = true
  try {
    strategy.value = await getRecommendationStrategy()
    fillForm(strategy.value.scheduled ?? strategy.value.active)
    loadError.value = ''
  } catch (error) {
    loadError.value = describeError(error, '推荐策略加载失败')
  } finally {
    loading.value = false
  }
}

async function save() {
  if (saving.value) return
  saving.value = true
  try {
    strategy.value = await updateRecommendationStrategy({ ...form })
    fillForm(strategy.value.scheduled ?? strategy.value.active)
    ElMessage.success(`策略已安排于 ${strategy.value.scheduledEffectiveDate ?? '次日'} 生效`)
  } catch (error) {
    ElMessage.error(describeError(error, '推荐策略保存失败'))
  } finally {
    saving.value = false
  }
}

onMounted(() => void load())
</script>

<template>
  <div class="strategy-page">
    <div class="panel">
      <div class="panel-title">
        个性化推荐策略
        <span class="actions">
          <el-button :icon="Refresh" :loading="loading" size="small" @click="load">刷新</el-button>
        </span>
      </div>

      <el-alert
        v-if="loadError"
        type="error"
        :closable="false"
        show-icon
        title="推荐策略加载失败"
        :description="loadError"
        style="margin-bottom: 16px"
      />
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="推荐由算法按每位用户的口味独立生成，不在这里人工创建歌单。"
        description="播放、跳过、收藏、喜欢/不喜欢和艺人偏好共同影响排序；不感兴趣的曲目会直接排除。此处修改从次日开始生效，当天已生成的推荐保持不变。"
        style="margin-bottom: 20px"
      />

      <div v-if="strategy" class="strategy-state">
        <div>
          <span class="text-faint">当前生效</span>
          <strong>{{ strategy.active.dailyTrackCount }} 首/人/日</strong>
        </div>
        <div v-if="strategy.scheduled">
          <span class="text-faint">已排期</span>
          <strong>{{ strategy.scheduledEffectiveDate }} · {{ strategy.scheduled.dailyTrackCount }} 首/人/日</strong>
        </div>
        <div v-else>
          <span class="text-faint">计划生效</span>
          <strong>保存后于次日生效</strong>
        </div>
      </div>

      <el-form v-loading="loading" label-position="top" class="strategy-form">
        <el-form-item label="每日推荐曲目数（5–50）">
          <el-input-number v-model="form.dailyTrackCount" :min="5" :max="50" />
        </el-form-item>
        <el-form-item label="艺人偏好权重（0–20）">
          <el-input-number v-model="form.artistAffinityWeight" :min="0" :max="20" :step="0.5" />
        </el-form-item>
        <el-form-item label="播放次数权重（0–20）">
          <el-input-number v-model="form.playCountWeight" :min="0" :max="20" :step="0.5" />
        </el-form-item>
        <el-form-item label="单曲播放加分封顶次数（1–100）">
          <el-input-number v-model="form.playCountCap" :min="1" :max="100" />
        </el-form-item>
        <el-form-item label="跳过惩罚（0–50）">
          <el-input-number v-model="form.skipPenalty" :min="0" :max="50" :step="0.5" />
        </el-form-item>
        <el-form-item label="收藏加分（0–50）">
          <el-input-number v-model="form.favoriteBoost" :min="0" :max="50" :step="0.5" />
        </el-form-item>
        <el-form-item label="喜欢反馈加分（0–50）">
          <el-input-number v-model="form.likeBoost" :min="0" :max="50" :step="0.5" />
        </el-form-item>
        <el-form-item label="不喜欢反馈惩罚（0–100）">
          <el-input-number v-model="form.dislikePenalty" :min="0" :max="100" :step="0.5" />
        </el-form-item>
        <el-form-item label="近期推荐降权天数（0–30）">
          <el-input-number v-model="form.nonRepeatDays" :min="0" :max="30" />
        </el-form-item>
        <el-form-item label="近期重复推荐惩罚（0–100）">
          <el-input-number v-model="form.recentRecommendationPenalty" :min="0" :max="100" :step="0.5" />
        </el-form-item>
        <el-form-item label="新歌加权衰减天数（1–365）">
          <el-input-number v-model="form.newTrackBoostDays" :min="1" :max="365" />
        </el-form-item>
        <el-form-item label="新歌发布当天最高加分（0–20）">
          <el-input-number v-model="form.newTrackBoostMax" :min="0" :max="20" :step="0.5" />
        </el-form-item>
      </el-form>

      <div class="form-actions">
        <el-button type="primary" :loading="saving" @click="save">保存并安排次日生效</el-button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.strategy-page {
  padding: 20px;
  max-width: 1000px;
}

.actions {
  margin-left: auto;
}

.strategy-state {
  display: flex;
  flex-wrap: wrap;
  gap: 24px;
  margin-bottom: 20px;
  padding: 14px 16px;
  border-radius: 8px;
  background: var(--el-fill-color-light);
}

.strategy-state > div {
  display: grid;
  gap: 4px;
}

.strategy-form {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
  column-gap: 20px;
}

.form-actions {
  display: flex;
  justify-content: flex-end;
  margin-top: 8px;
}

.text-faint {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
</style>
