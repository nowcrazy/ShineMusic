import { currentApiBase, getApiBaseOverride, setApiBaseOverride } from '@/services/http'

/**
 * 服务器地址编辑弹窗（个人抽屉与登录页共用）。
 * 登录页也提供入口，避免「内置地址不可达 → 无法登录 → 进不了设置改地址」的死锁。
 * 留空确认 = 清除自定义、恢复内置地址；连不通也会保存，便于提前填好新地址。
 */
export function promptServerBase(t: (key: string) => string, onChanged?: (base: string) => void) {
  uni.showModal({
    title: t('drawer.serverAddress'),
    editable: true,
    content: getApiBaseOverride(),
    placeholderText: t('drawer.serverPlaceholder'),
    confirmText: t('drawer.serverSaveTest'),
    success: ({ confirm, content }) => {
      if (!confirm) return
      setApiBaseOverride(content ?? '')
      const base = currentApiBase()
      onChanged?.(base)
      if (!getApiBaseOverride()) {
        uni.showToast({ title: t('drawer.serverRestored'), icon: 'none' })
        return
      }
      uni.showLoading({ title: t('drawer.serverTesting') })
      uni.request({
        url: `${base}/health`,
        method: 'GET',
        dataType: 'json',
        timeout: 8000,
        complete: (result) => {
          uni.hideLoading()
          const res = result as { statusCode?: number; data?: unknown }
          let payload: unknown = res.data
          if (typeof payload === 'string') {
            try { payload = JSON.parse(payload) } catch { /* HTML 等非 JSON 响应 */ }
          }
          // 只看状态码会误判：H5 未代理 /health 时 SPA fallback 返回 200 + index.html。
          // 后端探针固定返回 { status: 'ok' }，必须校验载荷才算真正连通。
          const isHealthPayload = typeof payload === 'object' && payload !== null &&
            (payload as Record<string, unknown>).status === 'ok'
          const code = res.statusCode
          const connected = !!code && code >= 200 && code < 300 && isHealthPayload
          uni.showToast({
            title: connected ? t('drawer.serverConnected') : t('drawer.serverNotConnected'),
            icon: 'none',
          })
        },
      })
    },
  })
}
