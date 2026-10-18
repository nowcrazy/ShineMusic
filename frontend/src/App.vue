<script setup lang="ts">
/**
 * 应用根组件（uni-app 生命周期入口）。
 *
 * 全局生命周期：
 * - onLaunch：应用启动时触发一次，负责恢复主题、恢复会话、加载收藏与能力开关。
 * - onShow  ：应用从后台回到前台时触发（本项目暂未使用，预留位置）。
 *
 * 全局样式通过 import './uni.scss' 引入 scss 变量与公共样式，
 * 下方 <style> 块定义主题令牌（CSS 变量）、页面基础样式与公共工具类。
 * 主题切换只需改写 <html data-sm-theme="...">，所有页面样式均引用令牌变量，
 * 因此切换时无需重新渲染页面即可整体换肤。
 */
import { onLaunch } from '@dcloudio/uni-app'
// 引入全局 scss 变量与公共样式（uni.scss）
import './uni.scss'
import { isAuthenticated, restoreSession } from '@/services/session'
import { loadCapabilities } from '@/services/capabilities'
import { loadFavorites } from '@/services/library'
import { loadFeedback } from '@/services/feedback'
import { initTheme } from '@/services/theme'

// 应用启动：先应用主题（避免首屏闪烁），再恢复登录态与能力开关
onLaunch(() => {
  initTheme()
  void (async () => {
    await restoreSession()
    if (isAuthenticated.value) {
      loadFavorites().catch(() => undefined)
      loadFeedback().catch(() => undefined)
    }
    loadCapabilities()
  })()
})
</script>

<style lang="scss">
/* ============================================================================
 * 主题令牌（CSS 变量）
 * ----------------------------------------------------------------------------
 * 四套主题：lime 绿意精致 / glass 玻璃质感（默认）/ cream 暖调奶油 / night 暗夜电台
 * 切换方式：document.documentElement.setAttribute('data-sm-theme', '<id>')
 * 新增主题：在下方追加一个 [data-sm-theme='<id>'] 块，并补齐全部令牌即可。
 *
 * 注意：本区块是令牌的「定义处」，必须写颜色字面量；
 * 其它页面的 <style> 只允许引用 var(--sm-*)。
 * ========================================================================== */

/* ---- 默认主题：glass 玻璃质感（深色底 + 彩色光晕 + 毛玻璃） ---- */
:root,
:root[data-sm-theme='glass'] {
  --sm-bg: #10131a;
  --sm-bg-soft: #161a24;
  --sm-page-bg: radial-gradient(circle at 8% 4%, rgba(91, 224, 122, .26), transparent 38%),
    radial-gradient(circle at 96% 20%, rgba(74, 140, 255, .26), transparent 42%),
    radial-gradient(circle at 4% 94%, rgba(176, 107, 255, .22), transparent 40%), #10131a;
  --sm-surface: rgba(255, 255, 255, .09);
  --sm-surface-2: rgba(255, 255, 255, .06);
  --sm-surface-3: rgba(255, 255, 255, .12);
  --sm-border: rgba(255, 255, 255, .16);
  --sm-border-strong: rgba(255, 255, 255, .22);
  --sm-divider: rgba(255, 255, 255, .08);

  --sm-text: #f2f5f0;
  --sm-text-2: rgba(255, 255, 255, .62);
  --sm-text-3: rgba(255, 255, 255, .45);

  --sm-accent: #5be07a;
  --sm-accent-ink: #0d1a10;
  --sm-accent-2: #4a8cff;
  --sm-accent-3: #b06bff;
  --sm-accent-soft: rgba(91, 224, 122, .16);
  --sm-accent-soft-border: rgba(91, 224, 122, .4);
  --sm-accent-grad: linear-gradient(90deg, #5be07a, #4a8cff);
  --sm-accent-glow: rgba(91, 224, 122, .3);

  --sm-danger: #ff8f6b;
  --sm-danger-soft: rgba(255, 143, 107, .16);
  --sm-danger-soft-border: rgba(255, 143, 107, .3);
  --sm-warn: #ffd166;
  --sm-warn-soft: rgba(255, 209, 102, .18);
  --sm-info-soft: rgba(74, 140, 255, .2);
  --sm-muted-soft: rgba(255, 255, 255, .12);
  --sm-accent-2-ink: #ffffff;
  --sm-accent-3-soft: rgba(176, 107, 255, .18);

  --sm-radius-card: 24rpx;
  --sm-radius-field: 16rpx;
  --sm-radius-pill: 40rpx;
  --sm-shadow: 0 16rpx 44rpx rgba(0, 0, 0, .4);
  --sm-shadow-sm: 0 8rpx 20rpx rgba(0, 0, 0, .3);
  --sm-blur: 20px;

  --sm-glow-opacity: .5;
  --sm-glow-1: #5be07a;
  --sm-glow-2: #4a8cff;
  --sm-glow-3: #b06bff;

  --sm-hero-bg: linear-gradient(135deg, rgba(91, 224, 122, .4), rgba(74, 140, 255, .34));
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .82) 0%, rgba(0, 0, 0, .42) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #10131a;
  --sm-avatar-bg: linear-gradient(135deg, rgba(91, 224, 122, .6), rgba(74, 140, 255, .55));
  --sm-cover-bg: linear-gradient(135deg, rgba(91, 224, 122, .42) 0%, rgba(74, 140, 255, .4) 100%);
  --sm-cover-ink: rgba(255, 255, 255, .9);
  --sm-overlay-ink: rgba(16, 19, 26, .5);
  --sm-mask: rgba(6, 8, 12, .62);

  --sm-nav-bg: #10131a;
  --sm-nav-text: #ffffff;
  --sm-tabbar-bg: rgba(16, 19, 26, .72);
  --sm-art-shadow: rgba(0, 0, 0, .42);
}

/* ---- 主题 A：lime 绿意精致（浅色底 + 青柠绿强调） ---- */
:root[data-sm-theme='lime'] {
  --sm-bg: #f7f8f4;
  --sm-bg-soft: #ffffff;
  --sm-page-bg: #f7f8f4;
  --sm-surface: #ffffff;
  --sm-surface-2: #f7f8f4;
  --sm-surface-3: #eef1e6;
  --sm-border: #e8eae2;
  --sm-border-strong: #d7dbcf;
  --sm-divider: #eef0e9;

  --sm-text: #23261f;
  --sm-text-2: #6b7168;
  --sm-text-3: #9aa094;

  --sm-accent: #c8f36a;
  --sm-accent-ink: #23261f;
  --sm-accent-2: #8fa8d6;
  --sm-accent-3: #d69a8f;
  --sm-accent-soft: #eef5dc;
  --sm-accent-soft-border: #c8f36a;
  --sm-accent-grad: linear-gradient(90deg, #a8cf55, #c8f36a);
  --sm-accent-glow: rgba(168, 207, 85, .45);

  --sm-danger: #d05a40;
  --sm-danger-soft: #f7e7e2;
  --sm-danger-soft-border: #ecd9d3;
  --sm-warn: #b8812f;
  --sm-warn-soft: #fdf3d6;
  --sm-info-soft: #e6edf8;
  --sm-muted-soft: #eef0ea;
  --sm-accent-2-ink: #2c3a55;
  --sm-accent-3-soft: #efe4f8;

  --sm-radius-card: 22rpx;
  --sm-radius-field: 16rpx;
  --sm-radius-pill: 40rpx;
  --sm-shadow: 0 14rpx 32rpx rgba(20, 24, 16, .1);
  --sm-shadow-sm: 0 6rpx 16rpx rgba(20, 24, 16, .07);
  --sm-blur: 0px;

  --sm-glow-opacity: 0;
  --sm-glow-1: #c8f36a;
  --sm-glow-2: #a8cf55;
  --sm-glow-3: #d4f0a3;

  --sm-hero-bg: linear-gradient(135deg, #3a4a26, #1f2a14);
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .78) 0%, rgba(0, 0, 0, .38) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: #c8f36a;
  --sm-hero-ink: #23261f;
  --sm-avatar-bg: linear-gradient(135deg, #d4f0a3, #a8cf55);
  --sm-cover-bg: linear-gradient(135deg, #d4f0a3 0%, #a8cf55 100%);
  --sm-cover-ink: #4a5c22;
  --sm-overlay-ink: rgba(255, 255, 255, .9);
  --sm-mask: rgba(20, 24, 16, .4);

  --sm-nav-bg: #f7f8f4;
  --sm-nav-text: #23261f;
  --sm-tabbar-bg: rgba(247, 248, 244, .94);
  --sm-art-shadow: rgba(20, 24, 16, .14);
}

/* ---- 主题 C：cream 暖调奶油（米杏底 + 陶土橙强调） ---- */
:root[data-sm-theme='cream'] {
  --sm-bg: #faf4ea;
  --sm-bg-soft: #fffaf2;
  --sm-page-bg: #faf4ea;
  --sm-surface: #ffffff;
  --sm-surface-2: #fdf7ee;
  --sm-surface-3: #f6ebdc;
  --sm-border: #f0e4d2;
  --sm-border-strong: #e4d0b4;
  --sm-divider: #f5ead9;

  --sm-text: #3a2f24;
  --sm-text-2: #8a7358;
  --sm-text-3: #b5a184;

  --sm-accent: #e8a87c;
  --sm-accent-ink: #4a3524;
  --sm-accent-2: #d9a05c;
  --sm-accent-3: #c98d6a;
  --sm-accent-soft: #fbe8d8;
  --sm-accent-soft-border: #e8a87c;
  --sm-accent-grad: linear-gradient(90deg, #e8a87c, #d9a05c);
  --sm-accent-glow: rgba(232, 168, 124, .42);

  --sm-danger: #c9553a;
  --sm-danger-soft: #f8e6df;
  --sm-danger-soft-border: #efd5c9;
  --sm-warn: #a8761f;
  --sm-warn-soft: #fbf1dc;
  --sm-info-soft: #ecf0f7;
  --sm-muted-soft: #f2ece3;
  --sm-accent-2-ink: #4a3524;
  --sm-accent-3-soft: #f3e6dc;

  --sm-radius-card: 26rpx;
  --sm-radius-field: 22rpx;
  --sm-radius-pill: 44rpx;
  --sm-shadow: 0 16rpx 34rpx rgba(200, 150, 100, .2);
  --sm-shadow-sm: 0 6rpx 16rpx rgba(200, 150, 100, .14);
  --sm-blur: 0px;

  --sm-glow-opacity: 0;
  --sm-glow-1: #f3d5b5;
  --sm-glow-2: #e0a878;
  --sm-glow-3: #f7e6d0;

  --sm-hero-bg: linear-gradient(135deg, #f3d5b5, #e0a878);
  --sm-hero-shade: linear-gradient(90deg, rgba(74, 53, 36, .72) 0%, rgba(74, 53, 36, .34) 52%, rgba(74, 53, 36, .03) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #4a3524;
  --sm-avatar-bg: linear-gradient(135deg, #f3d5b5, #e0a878);
  --sm-cover-bg: linear-gradient(135deg, #f3d5b5 0%, #e0a878 100%);
  --sm-cover-ink: #6b4a2e;
  --sm-overlay-ink: rgba(74, 53, 36, .5);
  --sm-mask: rgba(74, 53, 36, .42);

  --sm-nav-bg: #faf4ea;
  --sm-nav-text: #3a2f24;
  --sm-tabbar-bg: rgba(250, 244, 234, .94);
  --sm-art-shadow: rgba(200, 150, 100, .26);
}

/* ---- 主题 D：night 暗夜电台（深墨绿底 + 荧光青柠） ---- */
:root[data-sm-theme='night'] {
  --sm-bg: #14170f;
  --sm-bg-soft: #1b1f14;
  --sm-page-bg: radial-gradient(circle at 8% 4%, rgba(200, 243, 106, .16), transparent 38%),
    radial-gradient(circle at 96% 20%, rgba(47, 122, 74, .3), transparent 42%),
    radial-gradient(circle at 4% 94%, rgba(31, 107, 90, .26), transparent 40%), #14170f;
  --sm-surface: rgba(255, 255, 255, .07);
  --sm-surface-2: rgba(255, 255, 255, .045);
  --sm-surface-3: rgba(255, 255, 255, .11);
  --sm-border: rgba(255, 255, 255, .13);
  --sm-border-strong: rgba(255, 255, 255, .2);
  --sm-divider: rgba(255, 255, 255, .07);

  --sm-text: #eef3e2;
  --sm-text-2: rgba(238, 243, 226, .62);
  --sm-text-3: rgba(238, 243, 226, .44);

  --sm-accent: #c8f36a;
  --sm-accent-ink: #1a2010;
  --sm-accent-2: #7ce8a0;
  --sm-accent-3: #5be0c8;
  --sm-accent-soft: rgba(200, 243, 106, .14);
  --sm-accent-soft-border: rgba(200, 243, 106, .38);
  --sm-accent-grad: linear-gradient(90deg, #c8f36a, #7ce8a0);
  --sm-accent-glow: rgba(200, 243, 106, .32);

  --sm-danger: #ff9a6b;
  --sm-danger-soft: rgba(255, 154, 107, .16);
  --sm-danger-soft-border: rgba(255, 154, 107, .3);
  --sm-warn: #e8c96a;
  --sm-warn-soft: rgba(232, 201, 106, .16);
  --sm-info-soft: rgba(124, 232, 160, .14);
  --sm-muted-soft: rgba(255, 255, 255, .1);
  --sm-accent-2-ink: #1a2010;
  --sm-accent-3-soft: rgba(91, 224, 200, .14);

  --sm-radius-card: 20rpx;
  --sm-radius-field: 14rpx;
  --sm-radius-pill: 38rpx;
  --sm-shadow: 0 16rpx 40rpx rgba(0, 0, 0, .5);
  --sm-shadow-sm: 0 8rpx 20rpx rgba(0, 0, 0, .4);
  --sm-blur: 14px;

  --sm-glow-opacity: .38;
  --sm-glow-1: #c8f36a;
  --sm-glow-2: #2f7a4a;
  --sm-glow-3: #1f6b5a;

  --sm-hero-bg: linear-gradient(135deg, rgba(200, 243, 106, .32), rgba(47, 122, 74, .42));
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .84) 0%, rgba(0, 0, 0, .44) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: #c8f36a;
  --sm-hero-ink: #1a2010;
  --sm-avatar-bg: linear-gradient(135deg, rgba(200, 243, 106, .55), rgba(47, 122, 74, .6));
  --sm-cover-bg: linear-gradient(135deg, rgba(200, 243, 106, .4) 0%, rgba(47, 122, 74, .45) 100%);
  --sm-cover-ink: rgba(255, 255, 255, .88);
  --sm-overlay-ink: rgba(20, 23, 15, .55);
  --sm-mask: rgba(6, 8, 6, .66);

  --sm-nav-bg: #14170f;
  --sm-nav-text: #eef3e2;
  --sm-tabbar-bg: rgba(20, 23, 15, .74);
  --sm-art-shadow: rgba(0, 0, 0, .5);
}

/* ============================================================================
 * 基础样式（全部引用主题令牌）
 * ========================================================================== */
html,
body {
  background-color: var(--sm-bg);
}

page {
  min-height: 100%;
  background: var(--sm-bg);
  color: var(--sm-text);
  font-family: 'Avenir Next', Avenir, 'PingFang SC', 'Microsoft YaHei', sans-serif;
}

/* H5 原生导航栏：uni-app 会把 pages.json 的颜色写成行内样式，需 !important 覆盖 */
.uni-page-head {
  background-color: var(--sm-nav-bg) !important;
  color: var(--sm-nav-text) !important;
}

.uni-page-head__title,
.uni-page-head .uni-page-head-btn {
  color: var(--sm-nav-text) !important;
}

.uni-page-head .uni-page-head-btn svg path {
  fill: var(--sm-nav-text) !important;
}

/* ============================================================================
 * 公共工具类
 * ========================================================================== */

/* 卡片面（毛玻璃或实色，由 --sm-blur / --sm-surface 决定） */
.sm-glass {
  background: var(--sm-surface);
  border: 1px solid var(--sm-border);
  -webkit-backdrop-filter: blur(var(--sm-blur));
  backdrop-filter: blur(var(--sm-blur));
}

.sm-glass-strong {
  background: var(--sm-surface-3);
  border: 1px solid var(--sm-border-strong);
  -webkit-backdrop-filter: blur(var(--sm-blur));
  backdrop-filter: blur(var(--sm-blur));
}

/* 彩色光晕：页面背景装饰，置于内容之下；浅色主题下 --sm-glow-opacity 为 0 即不可见 */
.sm-glow-layer {
  position: fixed;
  inset: 0;
  z-index: 0;
  pointer-events: none;
  overflow: hidden;
}

.sm-glow {
  position: absolute;
  border-radius: 50%;
  filter: blur(60px);
  opacity: var(--sm-glow-opacity);
}

.sm-glow.green { background: var(--sm-glow-1); }
.sm-glow.blue { background: var(--sm-glow-2); }
.sm-glow.purple { background: var(--sm-glow-3); }
</style>
