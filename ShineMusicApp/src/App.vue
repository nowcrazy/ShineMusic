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
import { onHide, onLaunch, onShow } from '@dcloudio/uni-app'
// 引入全局 scss 变量与公共样式（uni.scss）
import './uni.scss'
import { isAuthenticated, restoreSession } from '@/services/session'
import { loadCapabilities } from '@/services/capabilities'
import { loadFavorites } from '@/services/library'
import { loadFeedback } from '@/services/feedback'
import { initTheme } from '@/services/theme'
import { flushPlayerState, syncFromBackgroundAudio } from '@/services/player'

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

// 从后台切回前台：同步 BackgroundAudioManager 的真实播放状态
// 可能的场景：Activity 重建（state 里 track 还是旧的但 BGM 已被锁屏切到 C），
// 或者 state.currentTime 落后于 BGM 实际进度。syncFromBackgroundAudio
// 会用 BGM 为权威源纠正 currentTrack / isPlaying / currentTime / duration，
// PlayerBar 绑定 usePlayer().state，纠正后 UI 自动更新。
onShow(() => {
  syncFromBackgroundAudio()
})

// 切到后台/即将退出时立即落盘播放现场，冷启动后可恢复曲目与进度
onHide(() => {
  flushPlayerState()
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

/* ---- 主题 E：sunset 落日橙（暖橘底 + 珊瑚橙强调） ---- */
:root[data-sm-theme='sunset'] {
  --sm-bg: #fdf3ec;
  --sm-bg-soft: #fffaf5;
  --sm-page-bg: #fdf3ec;
  --sm-surface: #ffffff;
  --sm-surface-2: #fdf5ee;
  --sm-surface-3: #f8e8dc;
  --sm-border: #f3e1d2;
  --sm-border-strong: #e9cdb6;
  --sm-divider: #f8ebdf;

  --sm-text: #3a2a22;
  --sm-text-2: #8a6d58;
  --sm-text-3: #b3937e;

  --sm-accent: #ff8a5c;
  --sm-accent-ink: #4a2416;
  --sm-accent-2: #f2a04d;
  --sm-accent-3: #e56d8a;
  --sm-accent-soft: #fde7dc;
  --sm-accent-soft-border: #ff8a5c;
  --sm-accent-grad: linear-gradient(90deg, #ff8a5c, #f2a04d);
  --sm-accent-glow: rgba(255, 138, 92, .42);

  --sm-danger: #cf4a36;
  --sm-danger-soft: #f9e5de;
  --sm-danger-soft-border: #efd2c6;
  --sm-warn: #a86f1f;
  --sm-warn-soft: #fbf0d9;
  --sm-info-soft: #e8f0f8;
  --sm-muted-soft: #f2e9e1;
  --sm-accent-2-ink: #4a2416;
  --sm-accent-3-soft: #f6e3e9;

  --sm-radius-card: 24rpx;
  --sm-radius-field: 18rpx;
  --sm-radius-pill: 42rpx;
  --sm-shadow: 0 16rpx 34rpx rgba(220, 130, 80, .2);
  --sm-shadow-sm: 0 6rpx 16rpx rgba(220, 130, 80, .14);
  --sm-blur: 0px;

  --sm-glow-opacity: 0;
  --sm-glow-1: #ffd9c4;
  --sm-glow-2: #f2a04d;
  --sm-glow-3: #f8c9d8;

  --sm-hero-bg: linear-gradient(135deg, #f2a04d, #ff8a5c);
  --sm-hero-shade: linear-gradient(90deg, rgba(74, 36, 22, .72) 0%, rgba(74, 36, 22, .34) 52%, rgba(74, 36, 22, .03) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #4a2416;
  --sm-avatar-bg: linear-gradient(135deg, #ffd9c4, #ff8a5c);
  --sm-cover-bg: linear-gradient(135deg, #ffd9c4 0%, #ff8a5c 100%);
  --sm-cover-ink: #7a3a1e;
  --sm-overlay-ink: rgba(74, 36, 22, .5);
  --sm-mask: rgba(74, 36, 22, .42);

  --sm-nav-bg: #fdf3ec;
  --sm-nav-text: #3a2a22;
  --sm-tabbar-bg: rgba(253, 243, 236, .94);
  --sm-art-shadow: rgba(220, 130, 80, .26);
}

/* ---- 主题 F：violet 紫罗兰（深紫底 + 霓虹紫） ---- */
:root[data-sm-theme='violet'] {
  --sm-bg: #15121f;
  --sm-bg-soft: #1c1829;
  --sm-page-bg: radial-gradient(circle at 8% 4%, rgba(157, 107, 255, .3), transparent 38%),
    radial-gradient(circle at 96% 20%, rgba(120, 90, 230, .3), transparent 42%),
    radial-gradient(circle at 4% 94%, rgba(220, 130, 240, .24), transparent 40%), #15121f;
  --sm-surface: rgba(255, 255, 255, .09);
  --sm-surface-2: rgba(255, 255, 255, .06);
  --sm-surface-3: rgba(255, 255, 255, .12);
  --sm-border: rgba(255, 255, 255, .16);
  --sm-border-strong: rgba(255, 255, 255, .22);
  --sm-divider: rgba(255, 255, 255, .08);

  --sm-text: #f2effa;
  --sm-text-2: rgba(242, 239, 250, .62);
  --sm-text-3: rgba(242, 239, 250, .44);

  --sm-accent: #9d6bff;
  --sm-accent-ink: #180d30;
  --sm-accent-2: #7aa2ff;
  --sm-accent-3: #e06bff;
  --sm-accent-soft: rgba(157, 107, 255, .18);
  --sm-accent-soft-border: rgba(157, 107, 255, .4);
  --sm-accent-grad: linear-gradient(90deg, #9d6bff, #7aa2ff);
  --sm-accent-glow: rgba(157, 107, 255, .34);

  --sm-danger: #ff8f8f;
  --sm-danger-soft: rgba(255, 143, 143, .16);
  --sm-danger-soft-border: rgba(255, 143, 143, .3);
  --sm-warn: #ffd166;
  --sm-warn-soft: rgba(255, 209, 102, .18);
  --sm-info-soft: rgba(122, 162, 255, .2);
  --sm-muted-soft: rgba(255, 255, 255, .12);
  --sm-accent-2-ink: #1a1030;
  --sm-accent-3-soft: rgba(224, 107, 255, .18);

  --sm-radius-card: 24rpx;
  --sm-radius-field: 16rpx;
  --sm-radius-pill: 40rpx;
  --sm-shadow: 0 16rpx 44rpx rgba(0, 0, 0, .46);
  --sm-shadow-sm: 0 8rpx 20rpx rgba(0, 0, 0, .34);
  --sm-blur: 20px;

  --sm-glow-opacity: .5;
  --sm-glow-1: #9d6bff;
  --sm-glow-2: #785ae6;
  --sm-glow-3: #dc82f0;

  --sm-hero-bg: linear-gradient(135deg, rgba(157, 107, 255, .4), rgba(122, 162, 255, .34));
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .82) 0%, rgba(0, 0, 0, .42) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #180d30;
  --sm-avatar-bg: linear-gradient(135deg, rgba(157, 107, 255, .6), rgba(122, 162, 255, .55));
  --sm-cover-bg: linear-gradient(135deg, rgba(157, 107, 255, .42) 0%, rgba(122, 162, 255, .4) 100%);
  --sm-cover-ink: rgba(255, 255, 255, .9);
  --sm-overlay-ink: rgba(20, 16, 34, .5);
  --sm-mask: rgba(8, 6, 16, .64);

  --sm-nav-bg: #15121f;
  --sm-nav-text: #f2effa;
  --sm-tabbar-bg: rgba(21, 18, 31, .72);
  --sm-art-shadow: rgba(0, 0, 0, .46);
}

/* ---- 主题 G：ocean 海洋蓝（雾蓝底 + 澄澈海蓝） ---- */
:root[data-sm-theme='ocean'] {
  --sm-bg: #eef4f8;
  --sm-bg-soft: #f7fbfd;
  --sm-page-bg: #eef4f8;
  --sm-surface: #ffffff;
  --sm-surface-2: #f3f8fb;
  --sm-surface-3: #e3eef5;
  --sm-border: #dfe9ef;
  --sm-border-strong: #c6d8e2;
  --sm-divider: #e8f0f4;

  --sm-text: #1f2a33;
  --sm-text-2: #5c7383;
  --sm-text-3: #8ca3b0;

  --sm-accent: #4aa3df;
  --sm-accent-ink: #0f2740;
  --sm-accent-2: #6fb7e8;
  --sm-accent-3: #4a7fe0;
  --sm-accent-soft: #e2eff8;
  --sm-accent-soft-border: #4aa3df;
  --sm-accent-grad: linear-gradient(90deg, #4aa3df, #6fb7e8);
  --sm-accent-glow: rgba(74, 163, 223, .4);

  --sm-danger: #d04a4a;
  --sm-danger-soft: #f9e4e4;
  --sm-danger-soft-border: #efd0d0;
  --sm-warn: #a8761f;
  --sm-warn-soft: #fbf0d9;
  --sm-info-soft: #e2eff8;
  --sm-muted-soft: #e9f1f5;
  --sm-accent-2-ink: #0f2740;
  --sm-accent-3-soft: #e4ecf9;

  --sm-radius-card: 22rpx;
  --sm-radius-field: 16rpx;
  --sm-radius-pill: 40rpx;
  --sm-shadow: 0 14rpx 32rpx rgba(40, 90, 120, .14);
  --sm-shadow-sm: 0 6rpx 16rpx rgba(40, 90, 120, .1);
  --sm-blur: 0px;

  --sm-glow-opacity: 0;
  --sm-glow-1: #c8e4f5;
  --sm-glow-2: #8fc8ec;
  --sm-glow-3: #b8d4f5;

  --sm-hero-bg: linear-gradient(135deg, #6fb7e8, #4a7fe0);
  --sm-hero-shade: linear-gradient(90deg, rgba(15, 39, 64, .76) 0%, rgba(15, 39, 64, .38) 52%, rgba(15, 39, 64, .03) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #0f2740;
  --sm-avatar-bg: linear-gradient(135deg, #c8e4f5, #6fb7e8);
  --sm-cover-bg: linear-gradient(135deg, #c8e4f5 0%, #6fb7e8 100%);
  --sm-cover-ink: #17445e;
  --sm-overlay-ink: rgba(15, 39, 64, .5);
  --sm-mask: rgba(15, 39, 64, .42);

  --sm-nav-bg: #eef4f8;
  --sm-nav-text: #1f2a33;
  --sm-tabbar-bg: rgba(238, 244, 248, .94);
  --sm-art-shadow: rgba(40, 90, 120, .22);
}

/* ---- 主题 H：sakura 樱花粉（樱粉底 + 落樱粉） ---- */
:root[data-sm-theme='sakura'] {
  --sm-bg: #fdf2f5;
  --sm-bg-soft: #fff8fa;
  --sm-page-bg: #fdf2f5;
  --sm-surface: #ffffff;
  --sm-surface-2: #fdf6f8;
  --sm-surface-3: #f8e6ec;
  --sm-border: #f6e0e7;
  --sm-border-strong: #f0c9d6;
  --sm-divider: #faecef;

  --sm-text: #3a2530;
  --sm-text-2: #8a5f70;
  --sm-text-3: #b68c9b;

  --sm-accent: #f18ab0;
  --sm-accent-ink: #4a1e30;
  --sm-accent-2: #f0a6c4;
  --sm-accent-3: #e07a9a;
  --sm-accent-soft: #fde6ee;
  --sm-accent-soft-border: #f18ab0;
  --sm-accent-grad: linear-gradient(90deg, #f18ab0, #f0a6c4);
  --sm-accent-glow: rgba(241, 138, 176, .42);

  --sm-danger: #d04a6a;
  --sm-danger-soft: #fae2e8;
  --sm-danger-soft-border: #f0cdd6;
  --sm-warn: #a8761f;
  --sm-warn-soft: #fbf0d9;
  --sm-info-soft: #eaeff8;
  --sm-muted-soft: #f5e9ed;
  --sm-accent-2-ink: #4a1e30;
  --sm-accent-3-soft: #f7e1ea;

  --sm-radius-card: 26rpx;
  --sm-radius-field: 20rpx;
  --sm-radius-pill: 44rpx;
  --sm-shadow: 0 16rpx 34rpx rgba(210, 120, 150, .18);
  --sm-shadow-sm: 0 6rpx 16rpx rgba(210, 120, 150, .12);
  --sm-blur: 0px;

  --sm-glow-opacity: 0;
  --sm-glow-1: #fcd5e0;
  --sm-glow-2: #f0a6c4;
  --sm-glow-3: #f8c9d8;

  --sm-hero-bg: linear-gradient(135deg, #f0a6c4, #f18ab0);
  --sm-hero-shade: linear-gradient(90deg, rgba(74, 30, 48, .7) 0%, rgba(74, 30, 48, .32) 52%, rgba(74, 30, 48, .03) 100%);
  --sm-hero-ink-bg: rgba(255, 255, 255, .94);
  --sm-hero-ink: #4a1e30;
  --sm-avatar-bg: linear-gradient(135deg, #fcd5e0, #f18ab0);
  --sm-cover-bg: linear-gradient(135deg, #fcd5e0 0%, #f18ab0 100%);
  --sm-cover-ink: #7a3050;
  --sm-overlay-ink: rgba(74, 30, 48, .5);
  --sm-mask: rgba(74, 30, 48, .42);

  --sm-nav-bg: #fdf2f5;
  --sm-nav-text: #3a2530;
  --sm-tabbar-bg: rgba(253, 242, 245, .94);
  --sm-art-shadow: rgba(210, 120, 150, .24);
}

/* ---- 主题 I：oled OLED 纯黑（纯黑底 + 霓虹青） ---- */
:root[data-sm-theme='oled'] {
  --sm-bg: #000000;
  --sm-bg-soft: #0a0a0c;
  --sm-page-bg: #000000;
  --sm-surface: rgba(255, 255, 255, .06);
  --sm-surface-2: rgba(255, 255, 255, .03);
  --sm-surface-3: rgba(255, 255, 255, .09);
  --sm-border: rgba(255, 255, 255, .12);
  --sm-border-strong: rgba(255, 255, 255, .18);
  --sm-divider: rgba(255, 255, 255, .05);

  --sm-text: #e8e8ea;
  --sm-text-2: rgba(232, 232, 234, .55);
  --sm-text-3: rgba(232, 232, 234, .38);

  --sm-accent: #45d0ff;
  --sm-accent-ink: #001c28;
  --sm-accent-2: #6c8cff;
  --sm-accent-3: #45ffd0;
  --sm-accent-soft: rgba(69, 208, 255, .14);
  --sm-accent-soft-border: rgba(69, 208, 255, .36);
  --sm-accent-grad: linear-gradient(90deg, #45d0ff, #6c8cff);
  --sm-accent-glow: rgba(69, 208, 255, .28);

  --sm-danger: #ff7a7a;
  --sm-danger-soft: rgba(255, 122, 122, .14);
  --sm-danger-soft-border: rgba(255, 122, 122, .28);
  --sm-warn: #ffd166;
  --sm-warn-soft: rgba(255, 209, 102, .16);
  --sm-info-soft: rgba(108, 140, 255, .18);
  --sm-muted-soft: rgba(255, 255, 255, .1);
  --sm-accent-2-ink: #00101c;
  --sm-accent-3-soft: rgba(69, 255, 208, .14);

  --sm-radius-card: 18rpx;
  --sm-radius-field: 12rpx;
  --sm-radius-pill: 36rpx;
  --sm-shadow: 0 16rpx 40rpx rgba(0, 0, 0, .7);
  --sm-shadow-sm: 0 8rpx 20rpx rgba(0, 0, 0, .5);
  --sm-blur: 16px;

  --sm-glow-opacity: .45;
  --sm-glow-1: #45d0ff;
  --sm-glow-2: #6c8cff;
  --sm-glow-3: #45ffd0;

  --sm-hero-bg: linear-gradient(135deg, rgba(69, 208, 255, .34), rgba(108, 140, 255, .3));
  --sm-hero-shade: linear-gradient(90deg, rgba(0, 0, 0, .84) 0%, rgba(0, 0, 0, .44) 52%, rgba(0, 0, 0, .04) 100%);
  --sm-hero-ink-bg: #45d0ff;
  --sm-hero-ink: #001c28;
  --sm-avatar-bg: linear-gradient(135deg, rgba(69, 208, 255, .5), rgba(108, 140, 255, .5));
  --sm-cover-bg: linear-gradient(135deg, rgba(69, 208, 255, .36) 0%, rgba(108, 140, 255, .36) 100%);
  --sm-cover-ink: rgba(255, 255, 255, .88);
  --sm-overlay-ink: rgba(0, 0, 0, .55);
  --sm-mask: rgba(0, 0, 0, .68);

  --sm-nav-bg: #000000;
  --sm-nav-text: #e8e8ea;
  --sm-tabbar-bg: rgba(0, 0, 0, .78);
  --sm-art-shadow: rgba(0, 0, 0, .6);
}

/* ============================================================================
 * 基础样式（全部引用主题令牌）
 * ========================================================================== */
html,
body {
  /* 页面底色用 --sm-page-bg（各主题的完整页面背景，自定义主题下为壁纸+遮罩），
     而非纯色 --sm-bg：纯色会盖住自定义图片主题的壁纸，导致换肤无感。
     注意必须用 background 简写：--sm-page-bg 在预设主题下是多层渐变，
     background-color 装不下渐变，整条声明会失效变透明。 */
  background: var(--sm-page-bg);
}

page {
  min-height: 100%;
  background: var(--sm-page-bg);
  color: var(--sm-text);
  font-family: 'Avenir Next', Avenir, 'PingFang SC', 'Microsoft YaHei', sans-serif;
}

/* ============================================================================
 * 折叠屏 / 平板适配
 * ----------------------------------------------------------------------------
 * 双折叠（Galaxy Fold / Mix Fold 等展开后约 700~900px）、三折叠（华为 Mate XT 等展开
 * 后约 1000px+）展开后的视口远宽于普通手机，全宽渲染会导致内容过度拉伸、固定元素错位。
 * 统一把页面内容收窄到 720px 并居中（与底部播放条 / 首页 tabbar 既有的 720px 上限一致），
 * 左右留白透出主题背景色，观感自然且不偏移。折叠后的外屏（约 340~420px）走 rpx 等比缩放，
 * 无需额外处理即可正常显示。
 * ========================================================================== */
/* #ifdef H5 */
/* 仅 H5 宽屏限宽；App 端（手机/折叠屏/平板）铺满，避免出现两侧空白与分隔线 */
@media (min-width: 700px) {
  page {
    max-width: 720px;
    margin: 0 auto;
  }
}
/* #endif */

/* ============================================================================
 * 宽视口适配（手机横持 / 折叠屏展开 / 平板）
 * ----------------------------------------------------------------------------
 * 触屏设备视口宽 ≥560px 时启用宽布局：手机横屏、折叠屏（双折/三折）展开、平板
 * 竖屏均覆盖；手机竖屏与折叠屏外屏不受影响，桌面浏览器仍走 H5 限宽规则。
 * 竖屏下额外要求宽高比 ≥ 2/3（视口接近方形）：折叠屏展开态约 0.83、平板约 0.69，
 * 而手机竖屏即便 CSS 宽度较大（低密度设备可达 640px）仍是 0.56 的窄长形，据此排除，
 * 保证「默认竖屏」不被宽布局误伤。
 * 宽视口下 uni-app 运行时按「视口宽度」换算 rpx，字号与间距会整体放大，一屏只能
 * 看到半个卡片。这里改用「短边」做基准，把字号/间距还原成竖屏手感（750/32 =
 * 23.4375，与运行时 fontSize = width/23.4375 同源）；同时把页面收成 720px 居中栏
 * （与桌面 H5 的限宽一致）。22px 封顶避免大屏字号过大。
 * 全屏播放器的宽视口双栏布局使用 px 尺寸，不受此基准影响。
 * ========================================================================== */
@media (orientation: landscape) and (max-height: 820px) and (min-width: 560px),
       (hover: none) and (pointer: coarse) and (min-width: 560px) and (min-aspect-ratio: 2/3) {
  html {
    font-size: min(calc(100vw / 23.4375), calc(100vh / 23.4375), 22px) !important;
  }
  /* 所有页面统一收成 720px 居中栏：横屏比竖屏宽一倍，铺满会把单列卡片拉扁 */
  page {
    max-width: 720px;
    margin: 0 auto;
  }
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
