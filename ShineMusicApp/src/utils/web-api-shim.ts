/**
 * Web API 兼容垫片（App 端必需）。
 *
 * uni-app 的 App（app-plus）逻辑层运行在 JSCore / V8 里，不是浏览器环境，
 * 没有 `URLSearchParams` 这类浏览器专有对象。服务层用 `new URLSearchParams()`
 * 拼接查询串（管理端曲目/导入、曲库列表、通知、需求等），在 App 上会直接抛
 * `ReferenceError: URLSearchParams is not defined`：请求根本不会发出，
 * 页面 catch 后只显示一句“加载失败”，后端日志也不会有任何记录。
 *
 * 这里在应用启动前补一个最小实现，只覆盖本项目实际用到的
 * set / append / get / has / delete / toString。H5 有原生实现，此垫片不生效。
 */

class MiniURLSearchParams {
  private pairs: Array<[string, string]> = []

  constructor(init?: string | Record<string, string>) {
    if (!init) return
    if (typeof init === 'string') {
      const source = init.startsWith('?') ? init.slice(1) : init
      if (!source) return
      for (const chunk of source.split('&')) {
        if (!chunk) continue
        const index = chunk.indexOf('=')
        const key = index < 0 ? chunk : chunk.slice(0, index)
        const value = index < 0 ? '' : chunk.slice(index + 1)
        this.pairs.push([decodeURIComponent(key.replace(/\+/g, ' ')), decodeURIComponent(value.replace(/\+/g, ' '))])
      }
      return
    }
    for (const key of Object.keys(init)) this.pairs.push([key, String(init[key])])
  }

  append(key: string, value: string): void {
    this.pairs.push([String(key), String(value)])
  }

  set(key: string, value: string): void {
    const name = String(key)
    const next: [string, string] = [name, String(value)]
    const index = this.pairs.findIndex((pair) => pair[0] === name)
    if (index < 0) this.pairs.push(next)
    else this.pairs[index] = next
  }

  get(key: string): string | null {
    const pair = this.pairs.find((item) => item[0] === String(key))
    return pair ? pair[1] : null
  }

  has(key: string): boolean {
    return this.pairs.some((item) => item[0] === String(key))
  }

  delete(key: string): void {
    const name = String(key)
    this.pairs = this.pairs.filter((item) => item[0] !== name)
  }

  toString(): string {
    return this.pairs
      .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
      .join('&')
  }
}

/** 补装 App 端缺失的浏览器全局对象；已存在时不做任何事。 */
export function installWebApiShim() {
  const scope = globalThis as Record<string, unknown>
  if (typeof scope.URLSearchParams === 'undefined') scope.URLSearchParams = MiniURLSearchParams
}
