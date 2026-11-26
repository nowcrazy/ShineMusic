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
| 通知栏 / 锁屏控制 | 需集成 `XZH-musicNotification` 原生插件 | ❌ 未实现 | Android 需含插件的自定义基座 / APK |
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

## Android 音乐通知插件与调试基座

`XZH-musicNotification` 是原生插件，标准 HBuilder 调试基座不包含它。未配置该插件时，适配层跳过原生调用；基础播放继续可用，插件提供的通知栏 / 锁屏歌曲信息与控制不会启用。

启用步骤：

1. 在 HBuilderX 打开 `src/manifest.json` 的可视化界面，在「App 原生插件配置」中选择并添加 `XZH-musicNotification`，按插件说明完成授权 / 参数配置；确认源码视图出现 `app-plus.nativePlugins.XZH-musicNotification`。由 HBuilderX 写入插件元数据。
2. 使用「运行 → 运行到手机或模拟器 → 制作自定义调试基座」，重新制作包含该插件的 Android 基座并安装到设备。
3. 将运行基座选择为「自定义基座」，停止并重新运行项目，使 manifest 对应的构建开关生效。仅热更新 JS 或继续使用标准基座，无法把原生插件装进设备。
4. 发布时也需重新云打包包含该插件的 APK；调试基座用于开发调试。

如果 manifest 已配置插件、设备却仍运行旧基座，框架可能在首次加载时提示一次缺失；适配层会缓存失败结果，后续播放 / 暂停 / 进度同步不再重试。更新基座后重新启动应用。

`vue-i18n` 的 esm-bundler 特性开关已在 `vite.config.ts` 配置。当前 DCloud 自带的旧版 i18n 运行时包含开发模式下恒真的告警判断，仍可能打印该提示；它不影响翻译或播放，生产构建会移除这段开发告警。

## 原件导出

“我的曲库”中的私有曲目提供原件下载入口。H5 通过带鉴权的请求获取完整文件，再交给浏览器保存；Android 通过原生下载器将文件暂存到应用私有目录，校验响应状态、音频类型和文件大小后，打开系统 `ACTION_CREATE_DOCUMENT` 选择保存位置，再以流复制原始字节。使用系统文档选择器无需申请共享存储访问权限。iOS 原生保存尚未实现。

Android 取消保存、下载失败或账号/服务器变更时，不显示成功提示，并清理下载临时文件；复制失败时会尝试删除不完整的目标文档，部分文档提供器可能不支持删除。原件导出不改写标签或歌词。合成桥回归使用 `node scripts/test-original-audio.mjs`，实际系统选择器、设备下载器及文档提供器仍须在 HBuilderX Android 真机验证。

## 已知边界

- `dev:h5` 不跑 `gen:icons`：lucide shim 仅 App 端 alias 使用，H5 用真实 lucide 包。
- **白屏兜底勿删**：`vite.config.ts` 已将 `'vue'` alias 到 `@dcloudio/uni-h5-vue/dist/vue.runtime.esm.js`（新编译器 resolveId bug 的兜底），删除即白屏。
- 本工程不配置 `build:h5` / H5 发版；如需 H5 发布，需另补生产 apiBase、CORS 与托管方案。
- `server.origin` 指向公网入口、但当前网络访问该域名返回 502（出网代理限制）时，H5 代理会一并失败；这是网络层限制，不是代理配置问题。纯本地调试可把 `origin` 临时改成 `http://127.0.0.1:5180` 并本机启动后端，调试完记得还原。
