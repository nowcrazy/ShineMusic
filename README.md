# Shine Music

一个以 Android 为首发目标、为 HarmonyOS NEXT 保留跨端空间的音乐应用。前后端放在同一仓库，通过目录隔离，分别开发和部署。

## 仓库结构

- `frontend/`：uni-app + Vue 3 客户端；首页数据访问集中在 `frontend/src/services`。
- `backend/`：ASP.NET Core Web API 后端服务与接口契约。
- `docs/backend-contract.md`：客户端与服务端的接口约定草案。

## 当前状态

- 已有精选首页、情绪筛选、曲目搜索、收藏状态、音频播放和全屏播放器。
- 未配置 API 时使用演示曲库；音频与封面是外部演示资源，不是正式曲库。
- 投稿入口暂未开放。投稿共享曲库必须接入版权声明、审核和对象存储后才能启用。
- 后端已有 .NET 10 + EF Core + SQLite 服务，公开曲库、Range 播放、邀请认证、收藏歌单、需求池和管理员导入发布流程已实现。
- 尚未在 Android 或 HarmonyOS NEXT 真机完成原生打包验证。

## 本地运行

要求 Node.js 18 或更高版本。

```sh
yarn --cwd frontend install
yarn dev
yarn typecheck
yarn build
```

H5 预览用于快速迭代界面。Android/HarmonyOS NEXT 原生运行和发行需要使用 uni-app 对应的 HBuilderX 工具链，并在目标设备上验证音频焦点、后台播放、锁屏控制、权限和资源域名配置。

## API 配置

**所有地址只在一个文件里维护：`config/hosts.json`**（前端构建与后端启动都读它）。

```jsonc
{
  "local":  { "label": "#Local", "lanIp": "192.168.31.145", "webPort": 5173, "apiPort": 5143, "appApiPort": 5180, "apiHost": "localhost" },
  "server": { "label": "#Server", "webOrigin": "https://music.your-domain.com", "apiOrigin": "https://music.your-domain.com" }
}
```

| 字段 | 作用 | 谁读取 |
|---|---|---|
| `local.lanIp` | 电脑局域网 IP | 前端代理/App、后端 CORS/邀请链接 |
| `local.webPort` | 前端端口 | 后端 CORS、邀请链接 |
| `local.apiPort` | 后端端口（本机） | 前端 dev 代理 |
| `local.appApiPort` | 真机 App 用的后端端口 | App 打包 |
| `local.apiHost` | `localhost` 或 `lan`（后端是否在别的机器） | 前端 dev 代理 |
| `server.webOrigin` | 生产前端域名 | 后端 CORS/邀请链接 |
| `server.apiOrigin` | 生产后端域名（同域时与 webOrigin 相同） | 生产构建 |

改 IP / 域名只改这一处，然后重启前后端即可。换 IP 的完整清单见 [docs/ip-change-checklist.md](docs/ip-change-checklist.md)。接口草案见 [docs/backend-contract.md](docs/backend-contract.md)。

后端启动和当前实现状态见 [backend/README.md](backend/README.md)。曲库契约见 [backend/api-list.md](backend/api-list.md) 与 [docs/backend-contract.md](docs/backend-contract.md)。