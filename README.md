# Shine Music

自建曲库音乐应用：不接第三方流媒体平台，单服务器部署，Android 首发（uni-app 跨端，为 HarmonyOS NEXT 保留打包位）。产品原则：**匿名可听、登录才互动、邀请制注册**。

## 仓库结构

- `ShineMusicApp/`：uni-app + Vue 3 + TypeScript 源码工程（H5 与 App 共用此源码），页面在 `src/pages`，服务层在 `src/services`
- `backend/`：ASP.NET Core (.NET 10) + EF Core + SQLite 后端
- `config/hosts.json`：前后端共用的唯一地址配置（不入库，模板见 `config/hosts.example.json`）
- `docs/`：项目文档（接口契约、操作指南）
- `.github/workflows/ci.yml`：CI（后端 Release 构建 + 前端契约漂移 / 类型检查 / H5 构建）

## 本地运行

```sh
# 1. 配置地址（新机器首次）：复制模板并把 lanIp 改成本机局域网 IP
Copy-Item config/hosts.example.json config/hosts.json

# 2. 后端（.NET 10 SDK，本机需可执行 ffmpeg / ffprobe）
dotnet run --project backend/ShineMusic.Api

# 3. 前端（Node 18+ / yarn，所有 yarn 命令在 ShineMusicApp 目录下执行）
cd ShineMusicApp
yarn install
yarn dev:h5        # H5 预览
yarn build:app     # APK 构建产物（再经 HBuilderX 云打包出安装包）
```

## 提交前必做

本地预检四道闸门（契约漂移 / 类型检查 / H5 构建 / 后端 Release 构建，与 CI 同源），命令与故障处理见 [docs/GitHub-CI处理指南.md](docs/GitHub-CI处理指南.md)。

## 地址配置

**所有地址只在 `config/hosts.json` 一处维护**（前端构建与后端启动都读它，读不到时有兜底，改完重启前后端生效）：

| 字段 | 作用 |
|---|---|
| `local.lanIp` | 电脑局域网 IP（dev 代理、后端 CORS、邀请邮件链接） |
| `local.apiPort` / `local.appApiPort` | 后端端口（本机 / 真机调试） |
| `server.appApiOrigin` | APK 内置的公网地址（改后必须重跑 `yarn build:app` 再打包） |

## 接口契约

- 接口清单：[docs/api-list.md](docs/api-list.md)
- 全局 DTO / 错误码 / 开关：[docs/domain-contract.md](docs/domain-contract.md)
- 后端实现说明：[docs/backend.md](docs/backend.md)

## APK 发布

`yarn build:app` 产出 `ShineMusicApp/dist/build/app` → HBuilderX 云打包 → 安装包**仅通过 GitHub Release 分发**，不入仓库。
