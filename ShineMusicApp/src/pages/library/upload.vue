<script setup lang="ts">
/**
 * 上传本地音频页：选择本地音频文件（可携带同名 .lrc 旁挂歌词）上传到“我的曲库”。
 *
 * 行为：
 * - 上传前按能力开关（allowedFormats / maxUploadSizeBytes）做格式与大小校验；
 * - 上传后按后端三分桶展示：Referenced（已存在，秒级加入曲库）/ Queued（处理中，转码+歌词匹配后私有入库）/ Rejected（他人私有曲目等）；
 * - 新歌 Queued 完成后会自动私有入库（仅本人可听），稍后可在“我的曲库”查看。
 *
 * 说明：旁挂 .lrc 应与音频同名（文件主干一致），后端按主干配对；不匹配的旁挂歌词会被忽略。
 * 进入参数：无。访问控制：onShow 时未登录直接跳登录页。
 * 对应后端接口：services/user-library（uploadFiles）；能力开关经 services/capabilities。
 */
import { computed, ref } from 'vue'
import { onShow } from '@dcloudio/uni-app'
import { useI18n } from 'vue-i18n'
import { Music4, Upload } from 'lucide-vue-next'
import { isAuthenticated } from '@/services/session'
import { useCapabilities } from '@/services/capabilities'
import { uploadFiles } from '@/services/user-library'
import type { ApiUserUploadResponse } from '@/types/music'

// 已选择的待上传文件（沿用 uni.chooseFile 的 tempFile 结构）
type PickedFile = { path: string; name: string; size: number }
// 能力开关单例
const caps = useCapabilities()

const { t } = useI18n()

// 已选文件列表 / 上传中 / 上传结果 / 错误信息
const files = ref<PickedFile[]>([])
const uploading = ref(false)
const result = ref<ApiUserUploadResponse | null>(null)
const errorMessage = ref('')

// 登录拦截
onShow(() => {
  if (!isAuthenticated.value) {
    uni.redirectTo({ url: '/pages/auth/login' })
  }
})

// 是否允许上传（开关开启且已选择音频文件）
const canUpload = computed(() => caps.capabilities.userUploads && hasAudio.value && !uploading.value)

// 是否已选择至少一个音频文件（排除纯旁挂歌词）
const hasAudio = computed(() => files.value.some((file) => isAudioFile(file.name)))

/**
 * 判断文件名是否为音频（非 .lrc/.txt 旁挂歌词）。
 * @param name 文件名
 * @returns 音频为 true
 */
function isAudioFile(name: string): boolean {
  const ext = name.split('.').pop()?.toLowerCase() ?? ''
  return ext !== 'lrc' && ext !== 'txt'
}

/**
 * 校验单文件：格式在允许列表内、大小未超限。
 * @param file 待校验文件
 * @returns 合法为 null，否则返回错误描述
 */
function validateFile(file: PickedFile): string | null {
  const ext = file.name.split('.').pop()?.toLowerCase() ?? ''
  const allowed = caps.capabilities.allowedFormats
  if (allowed.length && !allowed.includes(ext)) {
    return t('libraryPage.unsupportedFormat', { ext: `.${ext}`, allowed: allowed.join(' / ') })
  }
  const max = caps.capabilities.maxUploadSizeBytes
  if (max && file.size > max) {
    return t('libraryPage.fileTooLarge', {
      size: (file.size / 1024 / 1024).toFixed(1),
      max: (max / 1024 / 1024).toFixed(0),
    })
  }
  return null
}

/**
 * 选择本地文件：唤起系统选择器（count 上限 10），逐文件校验后加入待上传列表。
 * 关键副作用：非法文件弹 toast 不加入；合法文件加入 files。
 */
function handleChoose() {
  uni.chooseFile({
    count: 9,
    type: 'all',
    success: ({ tempFiles }) => {
      // uni 类型为 File | File[] | ChooseFileSuccessCallbackResultFile | ...，统一归一为数组
      const list = Array.isArray(tempFiles)
        ? tempFiles
        : tempFiles
          ? [tempFiles]
          : []
      const picked = list.map((file) => ({
        path: (file as { path?: string }).path ?? '',
        name: (file as { name?: string }).name ?? '',
        size: (file as { size?: number }).size ?? 0,
      }))
      const valid: PickedFile[] = []
      for (const file of picked) {
        const err = validateFile(file)
        if (err) {
          uni.showToast({ title: err, icon: 'none' })
          continue
        }
        valid.push(file)
      }
      if (valid.length) files.value = [...files.value, ...valid]
    },
  })
}

/**
 * 从待上传列表移除单个文件。
 * @param index 文件下标
 */
function handleRemoveFile(index: number) {
  files.value = files.value.filter((_, i) => i !== index)
}

/**
 * 执行上传：上传全部已选文件，成功后写入 result 并清空待上传列表。
 * 关键副作用：失败时写入 errorMessage；成功后清空 files 供再次上传。
 */
async function handleUpload() {
  if (!canUpload.value) return
  uploading.value = true
  errorMessage.value = ''
  result.value = null
  try {
    const response = await uploadFiles(files.value)
    result.value = response
    files.value = []
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : t('uploadPage.uploadFailedRetry')
  } finally {
    uploading.value = false
  }
}

/**
 * 完成后返回首页“曲库”tab 查看。
 * 上传页仅从首页曲库 tab 进入，返回即回到该 tab 且 onShow 自动刷新列表；
 * 若页面栈无上级（如直接进入），兜底重启到首页。
 */
function goBackToLibrary() {
  uni.navigateBack({
    fail: () => uni.reLaunch({ url: '/pages/home/index' }),
  })
}
</script>

<template>
  <view class="up-page">
    <!-- 开关关闭提示 -->
    <view v-if="!caps.capabilities.userUploads" class="state-text">
      {{ $t('libraryPage.uploadDisabled') }}
    </view>

    <template v-else>
      <!-- 说明卡片 -->
      <view class="up-tip">
        <view class="tip-icon"><Music4 :size="20" /></view>
        <view class="tip-copy">
          <text class="tip-title">{{ $t('library.upload') }}</text>
          <text class="tip-sub">{{ $t('libraryPage.privacyNote') }}</text>
        </view>
      </view>

      <!-- 已选文件列表 -->
      <view v-if="files.length" class="up-files">
        <view v-for="(file, index) in files" :key="file.path" class="file-row">
          <Music4 :size="18" class="file-icon" />
          <view class="file-copy">
            <text class="file-name">{{ file.name }}</text>
            <text class="file-size">{{ (file.size / 1024 / 1024).toFixed(2) }} MB</text>
          </view>
          <text class="file-remove" @tap.stop="handleRemoveFile(index)">{{ $t('uploadPage.removeFile') }}</text>
        </view>
      </view>

      <!-- 选择 / 上传操作 -->
      <view class="up-actions">
        <view class="btn btn-ghost" @tap="handleChoose"><Upload :size="17" /><text>{{ $t('libraryPage.chooseFiles') }}</text></view>
        <view class="btn btn-primary" :class="{ 'is-disabled': !canUpload }" @tap="handleUpload">
          {{ uploading ? $t('libraryPage.uploading') : $t('libraryPage.startUpload') }}
        </view>
      </view>

      <!-- 错误 -->
      <view v-if="errorMessage" class="up-error">{{ errorMessage }}</view>

      <!-- 上传结果 -->
      <view v-if="result" class="up-result">
        <view v-if="result.referenced.length" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultReferenced', { count: result.referenced.length }) }}</text>
          <text v-for="t in result.referenced" :key="t.trackId" class="result-line">· {{ t.title }}</text>
        </view>
        <view v-if="result.queued.length" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultQueued', { count: result.queued.length }) }}</text>
          <text v-for="q in result.queued" :key="q.itemId" class="result-line">· {{ q.displayName }} —— {{ $t('libraryPage.queuedHint') }}</text>
        </view>
        <view v-if="result.rejected.length" class="result-group">
          <text class="result-title">{{ $t('libraryPage.resultRejected', { count: result.rejected.length }) }}</text>
          <text v-for="r in result.rejected" :key="r.displayName" class="result-line">· {{ r.displayName }} —— {{ r.reason }}</text>
        </view>
        <view class="btn btn-primary" @tap="goBackToLibrary"><text>{{ $t('libraryPage.goToLibrary') }}</text></view>
      </view>
    </template>
  </view>
</template>

<style lang="scss" scoped>
/* 上传本地音频（方向 B · 玻璃质感） */
.up-page { min-height: 100vh; padding: 24rpx 40rpx 60rpx; background: var(--sm-page-bg); box-sizing: border-box; }
.state-text { padding: 80rpx 20rpx; color: var(--sm-text-2); font-size: 19rpx; text-align: center; }
.up-tip { display: flex; align-items: center; gap: 18rpx; margin-top: 8rpx; padding: 26rpx; border: 1rpx solid var(--sm-border); border-radius: 20rpx; background: var(--sm-surface); -webkit-backdrop-filter: blur(20px); backdrop-filter: blur(20px); }
.tip-icon { display: grid; flex: none; place-items: center; width: 70rpx; height: 70rpx; border-radius: 18rpx; background: var(--sm-accent-soft); color: var(--sm-accent); }
.tip-copy { display: flex; flex: 1; flex-direction: column; gap: 7rpx; }
.tip-title { color: var(--sm-text); font-size: 21rpx; font-weight: 700; }
.tip-sub { color: var(--sm-text-2); font-size: 16rpx; line-height: 1.5; }
.up-files { margin-top: 28rpx; }
.file-row { display: flex; align-items: center; gap: 14rpx; padding: 20rpx 0; border-bottom: 1rpx solid var(--sm-divider); }
.file-icon { flex: none; color: var(--sm-text-2); }
.file-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 5rpx; }
.file-name { overflow: hidden; color: var(--sm-text); font-size: 19rpx; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.file-size { color: var(--sm-text-3); font-size: 15rpx; }
.file-remove { flex: none; color: var(--sm-danger); font-size: 16rpx; font-weight: 600; }
.up-actions { display: flex; gap: 20rpx; margin-top: 36rpx; }
.btn { display: flex; flex: 1; align-items: center; justify-content: center; gap: 12rpx; height: 92rpx; border-radius: 46rpx; font-size: 20rpx; font-weight: 700; }
.btn-ghost { border: 1rpx solid var(--sm-border); background: var(--sm-surface); color: var(--sm-text); }
.btn-primary { border: 1rpx solid var(--sm-accent-soft-border); background: var(--sm-accent-soft); color: var(--sm-accent); }
.btn-primary.is-disabled { opacity: .5; }
.up-error { margin-top: 24rpx; padding: 18rpx 20rpx; border: 1rpx solid var(--sm-danger-soft-border); border-radius: 16rpx; background: var(--sm-danger-soft); color: var(--sm-danger); font-size: 16rpx; }
.up-result { margin-top: 32rpx; }
.result-group { margin-bottom: 20rpx; }
.result-title { display: block; margin-bottom: 10rpx; color: var(--sm-text); font-size: 19rpx; font-weight: 700; }
.result-line { display: block; padding: 6rpx 0; color: var(--sm-text-2); font-size: 17rpx; }
</style>
