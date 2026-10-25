/**
 * Web 端 uni 交互反馈实现：Toast / Loading / Modal / ActionSheet。
 * 全部用 DOM 直接渲染，样式引用主题令牌（--sm-*），保持与页面视觉一致。
 */

const Z_INDEX = 99990

function ensureHost(): HTMLDivElement {
  let host = document.getElementById('sm-uni-ui-host') as HTMLDivElement | null
  if (!host) {
    host = document.createElement('div')
    host.id = 'sm-uni-ui-host'
    document.body.appendChild(host)
  }
  return host
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, style: string, html?: string) {
  const node = document.createElement(tag)
  node.setAttribute('style', style)
  if (html !== undefined) node.innerHTML = html
  return node
}

function invoke(options: Record<string, unknown>, key: 'success' | 'fail' | 'complete', payload?: unknown) {
  const handler = options[key]
  if (typeof handler === 'function') (handler as (value?: unknown) => void)(payload)
}

let toastTimer = 0

export function showToast(options: Record<string, unknown>) {
  const host = ensureHost()
  let toast = document.getElementById('sm-uni-toast')
  if (toast) toast.remove()
  window.clearTimeout(toastTimer)

  const icon = String(options.icon ?? 'none')
  const title = String(options.title ?? '')
  const mark = icon === 'success' ? '✓ ' : icon === 'error' ? '✕ ' : ''
  const duration = Number(options.duration ?? 1500)

  toast = el(
    'div',
    `position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);z-index:${Z_INDEX};
     max-width:min(78vw,420px);padding:12px 18px;border-radius:12px;text-align:center;
     background:rgba(0,0,0,.78);color:#fff;font-size:14px;line-height:1.45;
     pointer-events:none;word-break:break-word;backdrop-filter:blur(10px);`,
    `${mark}${escapeHtml(title)}`,
  )
  toast.id = 'sm-uni-toast'
  host.appendChild(toast)
  toastTimer = window.setTimeout(() => {
    toast?.remove()
    invoke(options, 'success')
    invoke(options, 'complete')
  }, Math.max(600, duration))
}

export function hideToast() {
  window.clearTimeout(toastTimer)
  document.getElementById('sm-uni-toast')?.remove()
}

export function showLoading(options: Record<string, unknown> = {}) {
  hideLoading()
  const host = ensureHost()
  const title = String(options.title ?? '加载中…')
  const mask = el(
    'div',
    `position:fixed;inset:0;z-index:${Z_INDEX - 1};display:flex;align-items:center;justify-content:center;
     background:rgba(0,0,0,.35);backdrop-filter:blur(2px);`,
    `<div style="padding:16px 22px;border-radius:14px;background:rgba(0,0,0,.72);color:#fff;font-size:14px;
      display:flex;flex-direction:column;gap:10px;align-items:center;">
      <div style="width:26px;height:26px;border-radius:50%;border:2px solid rgba(255,255,255,.28);
        border-top-color:#fff;animation:sm-spin .8s linear infinite;"></div>
      <span>${escapeHtml(title)}</span>
    </div>`,
  )
  mask.id = 'sm-uni-loading'
  ensureSpinKeyframes()
  host.appendChild(mask)
}

export function hideLoading() {
  document.getElementById('sm-uni-loading')?.remove()
}

let spinReady = false
function ensureSpinKeyframes() {
  if (spinReady) return
  spinReady = true
  const style = document.createElement('style')
  style.textContent = '@keyframes sm-spin{to{transform:rotate(360deg)}}'
  document.head.appendChild(style)
}

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (ch) => `&#${ch.charCodeAt(0)};`)
}

export interface ModalResult {
  confirm: boolean
  cancel: boolean
}

export function showModal(options: Record<string, unknown>) {
  const host = ensureHost()
  const title = String(options.title ?? '提示')
  const content = String(options.content ?? '')
  const showCancel = options.showCancel !== false
  const confirmText = String(options.confirmText ?? '确定')
  const cancelText = String(options.cancelText ?? '取消')
  const confirmColor = String(options.confirmColor ?? 'var(--sm-accent, #5be07a)')

  const overlay = el(
    'div',
    `position:fixed;inset:0;z-index:${Z_INDEX};background:rgba(0,0,0,.5);display:flex;
     align-items:center;justify-content:center;padding:24px;`,
  )
  const card = el(
    'div',
    `width:min(88vw,420px);border-radius:16px;background:#1b1f28;color:#f2f5f0;
     box-shadow:0 24px 60px rgba(0,0,0,.45);overflow:hidden;`,
  )
  const body = el(
    'div',
    `padding:22px 22px 18px;text-align:center;`,
    `${title ? `<div style="font-size:17px;font-weight:600;margin-bottom:10px;">${escapeHtml(title)}</div>` : ''}
     <div style="font-size:14px;line-height:1.6;color:rgba(255,255,255,.78);white-space:pre-wrap;">${escapeHtml(content)}</div>`,
  )
  const footer = el('div', 'display:flex;border-top:1px solid rgba(255,255,255,.1);')
  const close = (confirm: boolean) => {
    overlay.remove()
    invoke(options, 'success', { confirm, cancel: !confirm } satisfies ModalResult)
    invoke(options, 'complete')
  }
  if (showCancel) {
    const cancel = el(
      'button',
      `flex:1;padding:14px;border:0;border-right:1px solid rgba(255,255,255,.1);background:transparent;
       color:rgba(255,255,255,.7);font-size:16px;cursor:pointer;`,
      escapeHtml(cancelText),
    )
    cancel.onclick = () => close(false)
    footer.appendChild(cancel)
  }
  const confirm = el(
    'button',
    `flex:1;padding:14px;border:0;background:transparent;color:${confirmColor};font-size:16px;font-weight:600;cursor:pointer;`,
    escapeHtml(confirmText),
  )
  confirm.onclick = () => close(true)
  footer.appendChild(confirm)
  card.append(body, footer)
  overlay.appendChild(card)
  host.appendChild(overlay)
}

export function showActionSheet(options: Record<string, unknown>) {
  const host = ensureHost()
  const items = Array.isArray(options.itemList) ? options.itemList.map((item) => String(item)) : []
  const overlay = el(
    'div',
    `position:fixed;inset:0;z-index:${Z_INDEX};background:rgba(0,0,0,.45);display:flex;align-items:flex-end;justify-content:center;`,
  )
  const sheet = el(
    'div',
    `width:100%;max-width:520px;background:#1b1f28;border-radius:16px 16px 0 0;overflow:hidden;color:#f2f5f0;
     padding-bottom:env(safe-area-inset-bottom,0px);`,
  )
  items.forEach((label, index) => {
    const row = el(
      'button',
      `display:block;width:100%;padding:15px;border:0;border-bottom:1px solid rgba(255,255,255,.08);
       background:transparent;color:#f2f5f0;font-size:15px;cursor:pointer;`,
      escapeHtml(label),
    )
    row.onclick = () => {
      overlay.remove()
      invoke(options, 'success', { tapIndex: index })
      invoke(options, 'complete')
    }
    sheet.appendChild(row)
  })
  const cancel = el(
    'button',
    `display:block;width:100%;padding:15px;border:0;background:rgba(255,255,255,.04);color:rgba(255,255,255,.72);
     font-size:15px;cursor:pointer;`,
    '取消',
  )
  const dismiss = () => {
    overlay.remove()
    invoke(options, 'fail', { errMsg: 'showActionSheet:fail cancel' })
    invoke(options, 'complete')
  }
  cancel.onclick = dismiss
  overlay.onclick = (event) => {
    if (event.target === overlay) dismiss()
  }
  sheet.appendChild(cancel)
  overlay.appendChild(sheet)
  host.appendChild(overlay)
}
