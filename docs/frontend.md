# 前端结构

客户端是 **uni-app 3 + Vue 3 + TypeScript + Vite**，包名 `com.shinemusic.app`。H5 用于界面迭代；Android / HarmonyOS 需 HBuilderX 原生打包。没有 Pinia：会话、播放器、收藏、能力开关都是 **模块级 reactive 单例**。

## 目录

```
frontend/src/
  pages/           # pages.json 注册的页面
  components/      # CoverArt、TrackRow、PlayerBar、ProfileDrawer
  services/        # 唯一对接后端的地方
  types/           # music.ts 视图模型 + api.generated.ts
  utils/           # 时间格式、H5 showModal 补丁
  App.vue          # 启动：主题、会话、收藏、反馈、capabilities
  uni.scss         # 全局样式；主题令牌在 App.vue
```

## 页面地图

首页 `pages/home/index` 自绘四个 Tab，不是原生 tabBar：

| Tab | 内容 |
|---|---|
| 为你 | 精选专题、风格筛选、最近加入、每日推荐入口 |
| 发现 | 风格浏览 + 防抖搜索 |
| 曲库 | 我的曲库（上传/引用，含私有） |
| 我的 | 资料、主题、管理入口、歌单、喜欢的音乐 |

其它页面：专辑/艺人/曲目/专题详情、歌单详情与选歌、点歌、消息、本地上传、主题、资料、登录/邀请注册。

管理页（仅 UI 按 `admin` 显示，API 仍鉴权）：

- `admin/import` 导入审核
- `admin/catalog` 曲目维护
- `admin/collections` 精选
- `admin/requests` 需求
- `admin/invitation` 邀请

## 服务层

| 模块 | 职责 |
|---|---|
| `http.ts` | uni.request 封装、baseURL、Bearer、401 刷新队列、`ApiError` |
| `session.ts` / `tokens.ts` | 登录态与令牌持久化 |
| `catalog.ts` | 公共曲库；`mapTrack` |
| `player.ts` | 全局播放队列与 InnerAudioContext |
| `library.ts` | 收藏与歌单 |
| `user-library.ts` | 我的曲库引用 |
| `requests.ts` / `notifications.ts` | 点歌与站内信 |
| `recommend.ts` / `feedback.ts` | 每日推荐与显式反馈 |
| `capabilities.ts` | 功能开关 |
| `theme.ts` | `data-sm-theme`：glass / lime / cream / night |
| `admin-*` | 管理端接口 |

H5 开发：`__SM_API_BASE__` 为空，请求相对路径，Vite 把 `/api`、`/health` 代理到 `config/hosts.json` 的本机 API。App 构建：注入 `http://{lanIp}:{appApiPort}`。生产：同域则空串（反代）；不同域用 `server.apiOrigin`。

## 播放器要点

- 全应用一个 `InnerAudioContext`，切页不停。
- 队列取模循环。
- 切歌上报 complete/skip；真正开始播放才报 play。
- 音频与封面 URL 必须经 `resolveUrl`，否则 App 上相对路径不可用。

## 主题

四套 CSS 变量主题写在 `App.vue`。业务样式只引用 `var(--sm-*)`，切换主题改 `html[data-sm-theme]`，不必重渲整树。

## 脚本

在 `frontend/` 目录下执行：

```sh
yarn install
yarn dev          # H5 浏览器预览
yarn typecheck
yarn build
```

App 端用 `yarn dev:app` / `yarn build:app`；契约用 `yarn gen:contracts` / `yarn check:contracts`。
