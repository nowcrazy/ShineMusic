/**
 * uni 风格 UI 反馈的浏览器实现：统一走 Element Plus，保证后台提示风格一致。
 */
import { ElLoading, ElMessage, ElMessageBox } from 'element-plus'

let loadingInstance: ReturnType<typeof ElLoading.service> | null = null

export function showToast(options: any) {
  const type = options?.icon === 'success' ? 'success' : options?.icon === 'error' ? 'error' : 'info'
  ElMessage({ message: String(options?.title ?? ''), type, duration: options?.duration ?? 2000 })
}

export function hideToast() {
  ElMessage.closeAll()
}

export function showLoading(options?: any) {
  loadingInstance?.close()
  loadingInstance = ElLoading.service({ fullscreen: true, text: options?.title ?? '' })
}

export function hideLoading() {
  loadingInstance?.close()
  loadingInstance = null
}

export function showModal(options: any) {
  ElMessageBox.confirm(String(options?.content ?? ''), String(options?.title ?? ''), {
    confirmButtonText: options?.confirmText ?? '确定',
    cancelButtonText: options?.cancelText ?? '取消',
    showCancelButton: options?.showCancel !== false,
  })
    .then(() => options?.success?.({ confirm: true, cancel: false }))
    .catch(() => options?.success?.({ confirm: false, cancel: true }))
    .finally(() => options?.complete?.())
}

/** 后台不使用动作面板，直接回调失败提示调用方降级。 */
export function showActionSheet(options: any) {
  options?.fail?.({ errMsg: 'showActionSheet:fail unsupported' })
  options?.complete?.()
}
