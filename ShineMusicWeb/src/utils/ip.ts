export type IpKind = 'public' | 'private' | 'shared' | 'loopback' | 'unknown'

export interface IpDescription {
  kind: IpKind
  label: string
  value: string
  display: string
  full: string
}

/** 展示后端记录的本次访问地址；不推测客户端是否开启代理，也不截断 IPv6。 */
export function describeIp(raw: string | null | undefined): IpDescription {
  const text = raw?.trim() ?? ''
  const unknown: IpDescription = { kind: 'unknown', label: '—', value: '', display: '—', full: '' }
  if (!text || text === '-' || text === '—' || text === '内网' || text === '共享地址' || text === 'VPN') return unknown

  if (text === '本机') {
    return { kind: 'loopback', label: '本机', value: '', display: '—', full: text }
  }

  let kind: IpKind = 'public'
  let value = text
  if (text.startsWith('内网 ')) {
    kind = 'private'
    value = text.slice(3).trim()
  } else if (text.startsWith('共享地址 ')) {
    kind = 'shared'
    value = text.slice(5).trim()
  } else if (text.startsWith('VPN ')) {
    // 兼容历史日志：100.64/10 属于共享地址，旧 VPN 标签并不能证明使用了翻墙代理。
    kind = 'shared'
    value = text.slice(4).trim()
  }

  if (!value || value === '-' || value === '—') return unknown

  const label = kind === 'private' ? '内网' : kind === 'shared' ? '共享地址' : '公网'
  return { kind, label, value, display: value, full: kind === 'shared' ? `${label} ${value}` : text }
}
