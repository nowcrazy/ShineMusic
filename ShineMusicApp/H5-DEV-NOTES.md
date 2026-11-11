# ShineMusicApp H5 开发调试指南

> 定位：H5 仅用于**开发期**快速调 UI 与业务流程（浏览器秒级热更新，无需打包 APK）。
> 发布仍以 APK 为准；正式 Web 前端是同级 `ShineMusicWeb` 工程。
> 凡涉及原生能力的行为，一律以 HBuilderX 真机运行为准。

## 启动

```bash
# 1. 先启动后端（默认 5180 端口）
cd ShineMusicApi/ShineMusic.Api && dotnet run

# 2. H5 dev server（默认 5173）
cd ShineMusicApp && yarn dev:h5
```

- 浏览器打开 `http://localhost:5173`
- 手机同 Wi-Fi 可访问 `http://<局域网IP>:5173`（`server.host: true` 已绑定 0.0.0.0，适合验证移动端浏览器的自动播放策略）
- API 走 Vite 代理：浏览器请求同源 `/api` → dev server 转发到 `config/hosts.json` 的 `server.origin`，浏览器侧同源无跨域。
- **地址只有一处**：`server.origin` 同时决定 H5 代理目标和 App 的 `apiBase`，换服务器时改这一处即可，两端自动跟随。配置里不含任何会变的本机局域网 IP。
- 纯本地、没有公网入口时，把 `origin` 填成 `http://127.0.0.1:5180`（后端就在本机）；`origin` 为空时也会自动回退到本机回环 `127.0.0.1:appApiPort`。
- 修改 `hosts.json` 会自动重启 dev server。

## 为什么 H5 和真机不一样

两端是**两个不同的运行环境**：

- App 端：代码跑在原生 App 的 WebView（plus 运行时）+ 原生插件里
- H5 端：代码跑在浏览器沙箱里，受自动播放策略、后台挂起、CORS 等浏览器规则约束

uni 同名 API 在两端是两套独立实现（uni-app-plus vs uni-h5），再叠加浏览器自身的限制，行为差异是必然的——这不是配置问题，任何跨端框架（Capacitor / RN / Flutter）都一样。本工程的做法是：**用条件编译（`#ifdef H5` / `#ifdef APP-PLUS`）为两端各写降级路径**。

## 能力对照表

| 能力 | App（真机） | H5 | 说明 |
|---|---|---|---|
| 前台播放 | BackgroundAudio / InnerAudioContext | WebAudio（支持 EQ） | `player.ts` 的 `createWebAudioEngine` |
| 后台 / 锁屏播放 | ✅ 系统级保活 | ❌ 切后台/锁屏即暂停 | 浏览器限制，无法绕过 |
| 通知栏 / 锁屏控制 | ✅ `XZH-musicNotification` 原生插件 | ❌ 未实现 | H5 可后续用 MediaSession API 补 |
| 物理返回键 | ✅ `plus.key backbutton` | ❌ 浏览器历史栈 | `App.vue` 已条件编译 |
| 文件选择 | `plus.android` 原生选择器 | `<input type="file">` | `app-file-picker.ts` |
| 本地文件 / 壁纸存储 | `plus.io` | ❌ 降级 | `theme.ts` |
| 图片压缩 | `uni.compressImage` 原生 | canvas 缩放转 blob | `image-compress.ts` |
| 语言推断 | 系统语言 | `navigator.language` | `locale/utils.ts` |
| 审计日志客户端类型 | `app` | `web-app` | `http.ts` 的 `X-Client-Type`，与后端 `ClientInfo.Resolve` 白名单对齐 |

## H5 特有注意事项

1. **自动播放策略**：Chrome/Edge 要求首次用户交互后才能出声，第一次点播放前可能无声，属正常现象。
2. **安全区 / 状态栏**：浏览器没有刘海，涉及 safe-area 的布局需用 devtools 设备模拟或真机验证。
3. **图标差异**：H5 用真实 `lucide-vue-next`（svg 渲染）；App 端因渲染桥不支持 svg，走生成的 shim——两端图标可能有细微观感差异。
4. **服务器地址覆盖**：设置页自定义过地址（`sm-server-base`）会绕过代理直连后端，浏览器会因 CORS 失败；调试期建议清除该存储键，走代理。
5. **封面 / 音频 404 不等于代理故障**：本地 `data/library` 为空（数据库里只有元数据、没有媒体文件）时，`/api/v1/artwork/{id}`、`/api/v1/tracks/{id}/stream`、`/api/v1/tracks/{id}/lyrics/default` 都会返回 404，但首页、目录、登录仍可正常渲染。判断代理是否通，看 `/api/v1/music/home`、`/api/v1/tracks`、`/api/v1/system/capabilities` 是否 200；要完整验证播放与封面，需先导入音频文件或把 `origin` 指向有媒体文件的服务器。

## 已知边界

- `dev:h5` 不跑 `gen:icons`：lucide shim 仅 App 端 alias 使用，H5 用真实 lucide 包。
- **白屏兜底勿删**：`vite.config.ts` 已将 `'vue'` alias 到 `@dcloudio/uni-h5-vue/dist/vue.runtime.esm.js`（新编译器 resolveId bug 的兜底），删除即白屏。
- 本工程不配置 `build:h5` / H5 发版；如需 H5 发布，需另补生产 apiBase、CORS 与托管方案。
- `server.origin` 指向公网入口、但当前网络访问该域名返回 502（出网代理限制）时，H5 代理会一并失败；这是网络层限制，不是代理配置问题。纯本地调试可把 `origin` 临时改成 `http://127.0.0.1:5180` 并本机启动后端，调试完记得还原。
