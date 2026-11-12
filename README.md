# Shine Music

自建曲库音乐应用：**播放只走自有曲库的音频流**，不做第三方流媒体在线播放；导入与搜源环节会访问外部音乐平台抓取音频与歌单元数据（11 个音源 provider、6 个歌单解析器）。单服务器部署，Android 首发（uni-app，为 HarmonyOS NEXT 保留打包位）。产品原则：**匿名可听、登录才互动、邀请制注册**。

## 仓库结构

一个共享后端 + 两个独立前端工程（各自独立构建产物，互不依赖）；App 另有一种**不发布**的浏览器调试形态 H5。

- `ShineMusicApi/`：ASP.NET Core (.NET 10) + SqlSugar + PostgreSQL + Redis + RabbitMQ，模块化单体后端。历史前缀 `/api/v1`，另提供 `/admin/v1`、`/app/v1` 双栈兼容入口（路由前重写，旧前缀不变）；`GET /health` 为匿名存活探针；**同时同域托管 `ShineMusicWeb/dist`**（dist 存在时启用，含 SPA fallback）
- `ShineMusicApi/ShineMusic.Worker/`：可选的独立 Media Worker 宿主，只跑音源下载 / 导入转码及其重试扫描；与 Web 进程用 `Worker:Mode` 分组，避免重复消费队列
- `ShineMusicApp/`：uni-app + Vue 3 的 **App 前端**，只发 APK（`yarn build:app`）；`yarn dev:h5` 提供同源码的浏览器开发调试形态，**没有 H5 发版构建**
- `ShineMusicWeb/`：Vue 3 + Vite + Element Plus 的 **Web 管理后台**，独立工程与登录页；生产产物由后端同域托管，不单独部署站点
- `ShineMusicApi/tools/generate-ts-contracts.py`：契约单一来源，一次生成同时写入 App/Web 两份 `src/types/api.generated.ts`；`--check` 用于防漂移
- `ShineMusicApi/tools/yt-dlp.exe` / `youtube-cookies.txt`：YouTube Music / B 站下载桥接所需，路径见 `appsettings.json` 的 `SourceDownload:YtDlp`
- `config/hosts.json`：前后端共用的唯一地址配置（不入库，模板 `config/hosts.example.json` 已入库）
- `docs/`：项目文档，见下方「文档索引」
- `.github/workflows/ci.yml`：CI 三个独立 job —— api（Release 构建且警告即错误 + 单元/架构测试）/ app（契约、类型、i18n、App 构建）/ web（契约、类型、构建）；api job 在 `ShineMusicApi/` 下执行

## 本地运行

前置：.NET 10 SDK、Node 22 + yarn、Python 3.12（契约生成器）、Docker Desktop、本机可执行 `ffmpeg` / `ffprobe`。

```sh
# 1. 配置地址（新机器首次；PowerShell 用 Copy-Item，bash 用 cp）
cp config/hosts.example.json config/hosts.json
# 把 server.origin 改成你的服务器地址；纯本地跑可填 http://127.0.0.1:5180

# 2. 基础设施：PostgreSQL 16 + Redis 7 + RabbitMQ 3
docker compose up -d

# 3. 后端（CodeFirst 幂等建表 + 角色/合集播种 + 恢复中断队列任务）
dotnet run --project ShineMusicApi/ShineMusic.Api

# 4. 首次创建管理员：另开终端执行，建表后创建账号即退出（不启动 Web 服务）
dotnet run --project ShineMusicApi/ShineMusic.Api -- --create-admin

# 5. Web 管理后台（http://localhost:5174）
cd ShineMusicWeb && yarn install && yarn dev

# 6. App 构建（产物再经 HBuilderX 云打包出安装包）
cd ../ShineMusicApp && yarn install && yarn build:app

# 可选：浏览器调试 App 同源码（http://localhost:5173）
cd ShineMusicApp && yarn dev:h5

# 可选：把媒体后台任务拆成独立进程（默认 all 时不需要这一步）
# 终端 A：Worker__Mode=web   dotnet run --project ShineMusicApi/ShineMusic.Api
# 终端 B：                  dotnet run --project ShineMusicApi/ShineMusic.Worker
```

`--create-admin` 会交互式询问邮箱 / 用户名 / 显示名 / 密码；非交互场景可用环境变量 `ADMIN_EMAIL`、`ADMIN_USERNAME`、`ADMIN_NAME`、`ADMIN_PASSWORD`。库尚未创建时可先加 `--create-database`。

| 服务 | 地址 | 说明 |
|---|---|---|
| API | `http://localhost:5180` | `launchSettings.json` 为 `0.0.0.0:5180`；`/health` 返回 `{"status":"ok"}`；开发环境另有 `/swagger` |
| Web 后台 dev | `http://localhost:5174` | Vite 只监听 localhost；`/api`、`/health` 走代理 |
| App H5 dev | `http://localhost:5173` | `server.host: true` 绑定 `0.0.0.0`，同 Wi-Fi 可访问；`/api`、`/health` 走代理 |
| 基础设施 | `5432` / `6379` / `5672` / `15672` | PostgreSQL / Redis / RabbitMQ AMQP / 管理台，仅本机或局域网 |

> **dev 代理指向 `server.origin`，不是本机 5180**：只要 `hosts.json` 里配了 origin，Web 与 H5 的 `/api`、`/health` 都转发到该地址；origin 留空才回退 `127.0.0.1:5180`。若本机后端与远程不同步，请求会打到远程实例。
> **Web dev 只监听 localhost（IPv6）**，用 `localhost` 而非 `127.0.0.1` 访问更稳。H5 的原生能力（后台播放、通知栏、SAF 选文件）不可用，一律以 HBuilderX 真机为准。

## 提交前必做

本地预检共八道闸门（契约漂移 / App 类型 / i18n 残留 / App 构建 / Web 类型 / Web 构建 / 后端 Release 构建且警告即错误 / 后端单元与架构测试），与 CI 三个 job 同源。完整命令与故障处理见 [docs/CI与GitHub提交流程.md](docs/CI与GitHub提交流程.md)。

常用命令（在对应前端目录执行）：`yarn check:contracts` / `yarn gen:contracts`、`yarn typecheck`、`yarn check:i18n`、`yarn build:app`（App）/ `yarn build:only`（Web）。

## 地址配置

**所有地址只在 `config/hosts.json` 的 `server.origin` 一处维护**：换成自己的服务器时，把这个值改成域名或 `http://服务器IP:端口`，App（dev 与 APK 内置地址一致）、H5 dev 代理、Web 后台 dev 代理与生产直连、后端 CORS 白名单、邀请邮件链接、Swagger 服务器地址全部自动跟随。配置里**不含任何会随 DHCP / 网卡变化的本机局域网 IP**。

| 字段 | 作用 |
|---|---|
| `server.origin` | **唯一服务器地址**（域名或 `http://服务器IP:端口`）。改后重跑 `yarn build:app` 再打 APK；App dev server 监听该文件会自动重启，Web dev server 需手动重启 |
| `local.apiPort` / `local.appApiPort` | 后端端口（默认 5180）；仅在 `origin` 未配置、纯本地开发（无服务器）时作为回退使用 |
| `local.webPort` | Web 前端 dev server 端口（默认 5174，后端据此放行 CORS） |

旧版字段 `local.lanIp` / `apiHost` / `server.webOrigin` / `apiOrigin` / `appApiOrigin` 已从模板移除，代码仅作兼容回退读取；新部署不需要填写它们。若只想纯本地跑（后端就在本机、没有公网入口），把 `origin` 填成 `http://127.0.0.1:5180`，或留空让代码回退到 `127.0.0.1:appApiPort`。

## 接口契约与文档

- 契约单一来源：`ShineMusicApi/tools/generate-ts-contracts.py` 从后端 C# `record` 生成 App/Web 两份 `src/types/api.generated.ts`。改动 DTO 后在任一前端执行 `yarn gen:contracts` 并提交两份产物；CI 用 `yarn check:contracts` 防漂移。
- API 历史前缀 `/api/v1`；双栈兼容入口 `/admin/v1/<rest>` → `/api/v1/admin/<rest>`、`/app/v1/<rest>` → `/api/v1/<rest>`，仅在路由前重写路径，鉴权、限流、错误体与 DTO 完全共用；`GET /health` 匿名存活探针；`GET /api/v1/system/capabilities` 返回非敏感能力开关快照。Swagger 仅开发环境在 `/swagger` 暴露。
- 鉴权三级：未标 `[Authorize]` 匿名、`[Authorize]` 需登录、`[Authorize(Roles="admin")]` 仅管理员。`capabilities` 只控制前端入口显隐，**授权始终由 API 执行**。
- **数据字典**：唯一交付 `docs/数据字典.chm`（Windows 编译帮助，40 表 / 335 列 / 9 业务域），含目录树、433 条关键字索引、全文查找页与逐列说明；旧的 `.md` / `.csv` / `.xml` / `.xsd` 已移除。重建命令 `python docs/generate-data-dictionary.py`（生成器从实体 XML 注释与状态机常量同源推导，默认只输出 CHM；`--format md,csv,xml` 可临时重建旧格式）。编译依赖微软 HTML Help Workshop 的 `hhc.exe`（仅 Windows，置于 `.workbuddy-ai/tools/htmlhelp/`）；**CI（ubuntu runner）不生成也不校验 CHM**，实体改名后须在本地重建并提交 `.chm`，字典本身不连库、不执行 DDL。

### 文档索引（`docs/` 现有文件）

| 文件 | 内容 |
|---|---|
| [功能清单.md](docs/功能清单.md) | 四端能力、路由、Controller / 端点、后台任务、功能开关；以源码为第一依据 |
| [系统进出口流程图.md](docs/系统进出口流程图.md) | 三端入口 / 出口边界，Mermaid 可编辑版 |
| [系统进出口流程图.html](docs/系统进出口流程图.html) | 同上，内嵌 SVG 的阅读 / 打印版 |
| [音源全景整理.md](docs/音源全景整理.md) | 解析 / 搜源下载 / 入库 / 播放四层、11 个音源 provider、6 个歌单解析器、存储布局与磁盘资产清点 |
| [CI与GitHub提交流程.md](docs/CI与GitHub提交流程.md) | 八道闸门本地预检、提交与 push 流程、排查表 |
| [数据字典.chm](docs/数据字典.chm) | 40 表 / 335 列 / 9 业务域的 Windows 编译帮助：目录树、433 条关键字索引、全文查找页与逐列说明 |
| [generate-data-dictionary.py](docs/generate-data-dictionary.py) | 数据字典生成器（唯一重建来源，默认输出 CHM；改实体后重跑） |
| [数据库命名重构方案.md](docs/数据库命名重构方案.md) | 表名 / 列名 snake_case 重构的决策与实施记录 |
| [数据库命名重构-迁移.sql](docs/数据库命名重构-迁移.sql) | 存量库幂等迁移脚本（须停服 + 备份后人工执行） |

> 早期文档 `docs/api-list.md`、`docs/domain-contract.md`、`docs/ShineMusicApi.md`、`docs/GitHub-CI处理指南.md`、`docs/上下文恢复-核心数据.md`、`docs/架构改造记录.md`、`docs/链路测试报告.md`、`docs/下游链路验证报告.md` 已不在当前工作区（历史工作区重建时丢失），请勿再引用；相关结论已并入本 README 与 `.workbuddy-ai/memory/`。

## 构建与发布

- **App**：`yarn build:app` → `ShineMusicApp/dist/build/app` → HBuilderX 云打包 → 安装包**仅通过 GitHub Release 分发**，不入仓库。H5 无发版构建。
- **Web 后台**：`yarn build:only` → `ShineMusicWeb/dist`；后端在 dist 存在时同域托管，与 API 同源，无需额外站点或跨域配置。
- **后端**：`dotnet build ShineMusicApi/ShineMusic.slnx -c Release` + `dotnet test ShineMusicApi/ShineMusic.slnx --no-build`；`Directory.Build.props` 已开启 `TreatWarningsAsErrors` + NetAnalyzers，任何新增告警直接失败。测试项目含领域规则、路径安全、双栈路由、后台任务分组与架构约束测试。
