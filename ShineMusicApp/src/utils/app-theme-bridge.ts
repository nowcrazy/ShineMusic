/**
 * App 端主题桥：service 层没有 document，主题令牌/属性需写入视图层 webview。
 *
 * 关键事实：App-vue 每个页面都是独立的 webview（各自有 document），
 * 因此主题必须写入「所有」webview，而不是只写 launch webview；
 * 新创建的页面（切换主题之后再进入的页面）由页面 onShow 时重放一次。
 *
 * H5 端不调用本模块（主题直接操作 document）。
 */

/** 最近一次写入的主题 id / 自定义样式，供新页面 onShow 时重放 */
let lastThemeId: string | null = null
let lastCustomCss: string | null = null

const THEME_ATTR_SCRIPT = (id: string) => `
(function () {
  var tries = 0;
  function go() {
    if (!document || !document.documentElement) {
      if (++tries < 100) setTimeout(go, 100);
      return;
    }
    document.documentElement.setAttribute('data-sm-theme', ${JSON.stringify(id)});
  }
  go();
})();`

const INJECT_CSS_SCRIPT = (css: string) => `
(function () {
  var tries = 0;
  function go() {
    if (!document || !document.head) {
      if (++tries < 100) setTimeout(go, 100);
      return;
    }
    var id = 'sm-custom-theme';
    var el = document.getElementById(id);
    if (!el) {
      el = document.createElement('style');
      el.id = id;
      document.head.appendChild(el);
    }
    el.textContent = ${JSON.stringify(css)};
  }
  go();
})();`

const REMOVE_CSS_SCRIPT = `
(function () {
  if (!document) return;
  var el = document.getElementById('sm-custom-theme');
  if (el && el.parentNode) el.parentNode.removeChild(el);
})();`

/** 收集当前所有视图 webview（含 launch webview；未就绪时为空） */
function listViews(): any[] {
  // #ifdef APP-PLUS
  // 与 app-file-picker 一致使用裸变量 plus：service 层能直接访问该全局
  if (typeof plus === 'undefined') return []
  const webviews = (plus as any).webview
  if (!webviews) return []
  const views: any[] = []
  const launch = webviews.getLaunchWebview?.()
  if (launch) views.push(launch)
  for (const view of webviews.all?.() ?? []) {
    if (view && !views.includes(view)) views.push(view)
  }
  return views
  // #endif
  return []
}

/** 把脚本写入所有视图；视图尚未就绪时短暂重试 */
function evalInViews(script: string, retries = 20) {
  // #ifdef APP-PLUS
  const views = listViews()
  if (!views.length) {
    if (retries > 0) setTimeout(() => evalInViews(script, retries - 1), 200)
    return
  }
  for (const view of views) {
    try {
      view.evalJS(script)
    } catch {
      // 某个视图还没加载完：忽略，页面 onShow 时会重放
    }
  }
  // #endif
}

/** 写入 <html data-sm-theme="id">（App 端，全部视图） */
export function setThemeAttrOnApp(id: string) {
  lastThemeId = id
  evalInViews(THEME_ATTR_SCRIPT(id))
}

/** 注入/更新自定义主题样式（App 端，全部视图） */
export function injectThemeCssOnApp(css: string) {
  lastCustomCss = css
  evalInViews(INJECT_CSS_SCRIPT(css))
}

/** 移除自定义主题样式（App 端，全部视图） */
export function removeThemeCssOnApp() {
  lastCustomCss = null
  evalInViews(REMOVE_CSS_SCRIPT)
}

/**
 * 重放主题到所有视图。
 * 页面 onShow 时调用：新页面（主题切换之后才创建的 webview）也能立即套上主题，
 * 避免「切了主题只有首页变化、其他页面还是默认皮肤」。
 */
export function reapplyThemeOnApp() {
  if (lastThemeId) evalInViews(THEME_ATTR_SCRIPT(lastThemeId), 5)
  if (lastCustomCss) evalInViews(INJECT_CSS_SCRIPT(lastCustomCss), 5)
}
