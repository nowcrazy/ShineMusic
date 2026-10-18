/**
 * H5 端 uni.showModal 关闭 bug 修复补丁。
 *
 * 问题：@dcloudio/uni-h5 3.0.0-5020620260917001 中，uni.showModal 点击按钮后
 *       success 回调正常执行，但模态框 DOM（#u-a-m > uni-modal）卡在
 *       uni-fade-leave-active 状态从未被移除，导致遮罩残留并阻塞后续所有 showModal。
 *
 * 修复：包装 uni.showModal 的 success/fail 回调，在用户点击按钮（回调触发）后
 *       强制移除 #u-a-m 容器，保证模态框一定能关闭。不劫持 complete，避免在
 *       模态框显示前就触发清理。
 */
export function patchShowModal() {
  // #ifdef H5
  if (typeof uni === 'undefined' || !uni.showModal) return
  if ((uni.showModal as any).__patched) return

  const original = uni.showModal.bind(uni) as any
  const patched = function (options: any) {
    const wrapped: any = { ...options }

    // 强制清理模态框 DOM：移除 #u-a-m 容器（uni-app H5 showModal 的挂载点）。
    const cleanup = () => {
      // 稍作延迟，确保用户回调中的同步逻辑（如 toast）先执行，再移除 DOM。
      setTimeout(() => {
        const el = document.getElementById('u-a-m')
        if (el) el.remove()
      }, 30)
    }

    // 无论用户点击确认还是取消，都要清理 modal。
    // success：点击确认/取消都会触发（confirm 字段区分）。
    if (typeof wrapped.success === 'function') {
      const origSuccess = wrapped.success
      wrapped.success = (res: any) => {
        try { origSuccess(res) } finally { cleanup() }
      }
    } else {
      wrapped.success = cleanup
    }

    // fail：调用失败时也清理。
    if (typeof wrapped.fail === 'function') {
      const origFail = wrapped.fail
      wrapped.fail = (res: any) => {
        try { origFail(res) } finally { cleanup() }
      }
    } else {
      wrapped.fail = cleanup
    }

    // 不劫持 complete，避免在模态框创建过程中被误清理。

    return original(wrapped)
  }
  patched.__patched = true
  uni.showModal = patched
  // #endif
}
