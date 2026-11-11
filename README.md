# Shine Music

自建曲库音乐应用：不接第三方流媒体平台，单服务器部署，Android 首发（uni-app 跨端，为 HarmonyOS NEXT 保留打包位）。产品原则：**匿名可听、登录才互动、邀请制注册**。

## 仓库结构

三段式：**一个共享后端 + 两个独立前端**，前端各自拥有独立工程与构建产物，互不依赖。

- `ShineMusicApi/`：ASP.NET Core (.NET 10) + SqlSugar + PostgreSQL + Redis + RabbitMQ，唯一 API 服务
- `ShineMusicApp/`：uni-app + Vue 3 的 **App 前端**（只产 APK，无 H5 构建）
- `ShineMusicWeb/`：Vue 3 + Vite + Element Plus 的 **Web 管理后台**（独立站点与登录，面向管理员）
- `ShineMusicApi/tools/generate-ts-contracts.py`：契约单一来源，一次生成同时写入 App/Web 两份 `api.generated.ts`
- `config/hosts.json`：前后端共用的唯一地址配置（不入库，模板见 `config/hosts.example.json`）
- `docs/`：项目文档（接口契约、操作指南）
- `.github/workflows/ci.yml`：CI（api / app / web 三个独立 job，其中 api job 在 `ShineMusicApi/` 下执行）

## 本地运行

```sh
# 1. 配置地址（新机器首次）：复制模板，把 server.origin 改成你的服务器地址
Copy-Item config/hosts.example.json config/hosts.json

# 2. 数据库、缓存与消息队列（Docker Desktop）：启动 PostgreSQL 16 + Redis 7 + RabbitMQ 3
docker compose up -d

# 3. 后端（.NET 10 SDK，本机需可执行 ffmpeg / ffprobe；启动时 SqlSugar CodeFirst 幂等建表）
dotnet run --project ShineMusicApi/ShineMusic.Api

# 4. Web 管理后台（http://localhost:5174，独立登录页；/api 由 Vite 代理到后端）
cd ShineMusicWeb
yarn install
yarn dev

# 5. App 前端（构建产物再经 HBuilderX 云打包出安装包）
cd ../ShineMusicApp
yarn install
yarn build:app
```

## 提交前必做

本地预检（契约漂移 / 两个前端类型检查 / Web 构建 / App 构建 / 后端 Release 构建，与 CI 同源），命令与故障处理见 [docs/GitHub-CI处理指南.md](docs/GitHub-CI处理指南.md)。

## 地址配置

**所有地址只在 `config/hosts.json` 的 `server.origin` 一处维护**：换成自己的服务器时，把这个值改成域名或 `http://服务器IP:端口`，App（dev 与 APK 内置地址一致）、H5 dev 代理、Web 后台 dev 代理与生产直连、后端 CORS 白名单、邀请邮件链接、Swagger 公网入口全部自动跟随。配置里**不含任何会随 DHCP / 网卡变化的本机局域网 IP**。

| 字段 | 作用 |
|---|---|
| `server.origin` | **唯一服务器地址**（域名或 `http://服务器IP:端口`）。改后重跑 `yarn build:app` 再打 APK；dev server 会自动重启 |
| `local.apiPort` / `local.appApiPort` | 后端端口；仅在 `origin` 未配置、纯本地开发（无服务器）时作为回退使用 |
| `local.webPort` | Web 前端 dev server 端口（后端据此放行 CORS） |

旧版字段 `local.lanIp` / `apiHost` / `server.webOrigin` / `apiOrigin` / `appApiOrigin` 已从模板移除，代码仅作兼容回退读取；新部署不需要填写它们。若只想纯本地跑（后端就在本机、没有公网入口），把 `origin` 填成 `http://127.0.0.1:5180` 即可。

## 接口契约

- 接口清单：[docs/api-list.md](docs/api-list.md)
- 全局 DTO / 错误码 / 开关：[docs/domain-contract.md](docs/domain-contract.md)
- 后端实现说明：[docs/ShineMusicApi.md](docs/ShineMusicApi.md)

## APK 发布

`yarn build:app` 产出 `ShineMusicApp/dist/build/app` → HBuilderX 云打包 → 安装包**仅通过 GitHub Release 分发**，不入仓库。
