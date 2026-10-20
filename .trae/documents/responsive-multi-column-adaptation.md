# 全机型响应式适配（手机 / 平板 / 折叠屏）实施方案

## Context（背景）

当前 Shine Music 前端是 uni-app H5（Vue3 + TS + scss），全站使用 rpx 尺寸（1rpx = 屏宽 / 750）＋ CSS 变量主题令牌 `--sm-*`。rpx 天然适配手机竖屏，但存在三个在**平板 / 折叠屏展开（屏宽 700~1200px+）**下会「偏移、拉伸、错位」的根因：

1. **rpx 等比放大**：宽屏下字号、间距、卡片被等比放大到超出预期，单列 `rpx` 固定宽度的卡片被拉伸到满屏。
2. **只有 3 处做了宽屏约束**，且都是「居中锁定 720px」（首页 `.app-shell`、PlayerBar `.now-playing`/`.player-overlay`、requests 表单 `.form-sheet`）；其余 20+ 页面与固定定位元素（抽屉、遮罩等）完全没有宽屏处理。
3. **视口高度 / 安全区不完整**：多处 `100vh` 在移动浏览器地址栏收起/展开时高度不准；多数页面未处理顶部/底部 `safe-area`。

用户已确认采用**「完全响应式双栏」**策略：折叠屏展开 / 平板横屏时充分利用大屏（网格多列、卡片尺寸分级），而非居中锁定。

## 方案设计

### 1. 断点体系（定义在 `src/uni.scss`，SCSS mixin，零产物）

在 [uni.scss](file:///c:/Learn/ShineMusic/frontend/src/uni.scss) 末尾追加断点 mixin（uni.scss 会被注入到每个组件的 `<style lang="scss">`，mixin 定义不产出 CSS，仅在 @include 处展开，安全且全站可用）：

- `@mixin sm-wide { @media (min-width: 700px) { @content } }` — 折叠屏展开 / 小平板
- `@mixin sm-ultra { @media (min-width: 1024px) { @content } }` — 平板横屏 / 桌面

### 2. 页面分层策略（区分「多栏页」与「焦点页」）

- **多栏页（列表/网格/信息流）**：首页四个 tab（为你/发现/曲库/我的）、catalog 列表、歌单/专题详情、notifications、requests 列表。宽屏下将单列/横滑容器转成 CSS Grid 多列。
- **焦点页（表单/详情焦点）**：login / register / upload / playlist 详情头、profile、admin 表单。宽屏下保持居中，仅适度放宽最大宽度（约 860px），避免满屏拉伸。

### 3. 统一网格工具类（定义在 `App.vue` 全局非 scoped `<style>`）

在 [App.vue](file:///c:/Learn/ShineMusic/frontend/src/App.vue) 全局 style 内追加响应式网格工具类，供各页面复用：

```scss
@include 对应断点时（或写全局类）：
.sm-grid-2 { display: grid; gap: 24rpx; grid-template-columns: 1fr; }
@include sm-wide { .sm-grid-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@include sm-ultra { .sm-grid-2 { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
```

横滑区（风格 chips、专题、推荐卡片）在宽屏下用 `flex-wrap: wrap` 替代 `scroll-x`，让卡片换行平铺。

### 4. 固定定位元素宽屏策略

全宽多栏形态下，以下 fixed 元素**全宽**，不居中（移除/覆盖现有 720px 居中）：

- 首页 `.tabbar`（[home/index.vue](file:///c:/Learn/ShineMusic/frontend/src/pages/home/index.vue#L1164-L1171)）：删掉或覆盖宽屏下的 `max(0, calc(100vw-720px)/2)` 左右约束，保持 `left:0;right:0`。
- PlayerBar `.now-playing`（[PlayerBar.vue](file:///c:/Learn/ShineMusic/frontend/src/components/PlayerBar.vue#L674-L678)）：同样改为全宽贴底。

以下 fixed 元素**限制宽度**（避免宽屏下 rpx 撑爆）：

- ProfileDrawer `.drawer-panel`（[ProfileDrawer.vue](file:///c:/Learn/ShineMusic/frontend/src/components/ProfileDrawer.vue#L260)）：宽度 `640rpx` 在宽屏加 `max-width: 420px` 上限。
- 各弹层遮罩（create-sheet、invitation 注册码弹层）已用 `place-items:center` / `justify-content:center`，保持居中即可。

### 5. 视口高度与安全区（基础健壮性）

- `height:100vh` / `min-height:100vh` → 补 `100dvh` 回退写法（`height:100vh; height:100dvh;`），覆盖 home `.page-scroll`、profile `.profile-page` 等代表点。
- 补齐缺失的 `env(safe-area-inset-top)`（自定义导航页 hero/返回栏）与 `safe-area-inset-bottom`（tabbar 已有、PlayerBar 已有，抽查其余底部吸底元素）。

## 实施步骤

### L0 — 基础（全站「不错位」的地基）

1. 在 `uni.scss` 追加断点 mixin `sm-wide` / `sm-ultra`。
2. 在 `App.vue` 全局 style 追加 `.sm-grid-2`、`.sm-flex-wrap` 等响应式工具类。
3. 覆盖/移除 3 处既有 720px 居中（home `.app-shell`、PlayerBar、requests `.form-sheet`），改为全宽基础 + 内部网格。
4. ProfileDrawer `.drawer-panel` 加 `max-width`。
5. 视口高度 100dvh 回退 + 安全区补齐（代表文件：home、profile）。

### L1 — 首页双栏（核心交付）

[home/index.vue](file:///c:/Learn/ShineMusic/frontend/src/pages/home/index.vue) 四个 tab 逐一网格化：

- **为你**：hero/inspiration-card、featured、mood chips（换行）、recommend、collection（换行或双栏）。
- **发现**：collections 横滑 → 网格；genreChips 换行；搜索/需求入口保持。
- **曲库**：我的曲库列表 → 网格卡片；歌单列表 → 网格。
- **我的**：profile-card + 歌单/收藏列表 → 双栏网格。

对每个 section 容器加 `.sm-grid-2`（或断点 mixin 内写 grid），卡片子元素用 `minmax(0, 1fr)` 防溢出。

### L2 — 列表 / 详情页（遵循同一模式）

对 catalog（tracks/album/artist/collection）、playlist detail、notifications、requests 等列表页，遵循「多栏页」模式：列表容器转网格、卡片尺寸分级。按重复模式描述，不逐文件列行号。

### L3 — 焦点页（表单 / 详情）

login、register、upload、admin 表单、profile 等保持居中，`@include sm-wide` 内将根容器 `max-width` 放宽到约 860px 居中。

## 验证

1. `npm run build`（frontend）确认无编译错误。
2. 用 browser_use 子代理在至少 3 个 viewport 截图核对：手机（375×812）、折叠屏展开（≈ 717×780 与 800×900）、平板横屏（1024×768 与 1280×800），重点确认首页四 tab、抽屉、PlayerBar、tabbar 无错位/溢出。
3. 切换 9 套主题抽查网格容器背景/留白是否随令牌协调。

## 关键文件

- `src/uni.scss`（断点 mixin）
- `src/App.vue`（全局网格工具类 + 100dvh + fixed 策略）
- `src/pages/home/index.vue`（首页四 tab 双栏，核心）
- `src/components/PlayerBar.vue`、`src/components/ProfileDrawer.vue`（fixed 元素宽屏）
- 列表/详情页遵循同一网格模式（catalog、playlist、notifications、requests）