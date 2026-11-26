# Shine Music

自建曲库音乐应用：**播放只走自有曲库的音频流**，不做第三方流媒体在线播放；导入与搜源环节会访问外部音乐平台抓取音频与歌单元数据（11 个音源 provider、6 个歌单解析器）。单服务器部署，Android 首发（uni-app，为 HarmonyOS NEXT 保留打包位）。产品原则：**匿名可听、登录才互动、邀请制注册**。

## 仓库结构

一个共享后端 + 两个独立前端工程（各自独立构建产物，互不依赖）；App 另有一种**不发布**的浏览器调试形态 H5。

- `ShineMusicApi/`：ASP.NET Core (.NET 10) + SqlSugar + PostgreSQL + Redis + RabbitMQ，模块化单体后端。历史前缀 `/api/v1`，另提供 `/admin/v1`、`/app/v1` 双栈兼容入口（路由前重写，旧前缀不变）；`GET /health` 为匿名存活探针；**同时同域托管 `ShineMusicWeb/dist`**（dist 存在时启用，含 SPA fallback）
- `ShineMusicApi/ShineMusic.Worker/`：可选的独立 Media Worker 宿主，只跑音源下载 / 导入转码及其重试扫描；与 Web 进程用 `Worker:Mode` 分组，避免重复消费队列
- `ShineMusicApp/`：uni-app + Vue 3 的 **App 前端**，只发 APK（`yarn build:app`）；`yarn dev:h5` 提供同源码的浏览器开发调试形态，**没有 H5 发版构建**
- `ShineMusicWeb/`：Vue 3 + Vite + Element Plus 的 **Web 管理后台**，独立工程与登录页；生产产物由后端同域托管，不单独部署站点
- `ShineMusicApi/tools/generate-ts-contracts.py`：契约单一来源，一次生成同时写入 App/Web 两份 `src/types/api.generated.ts`；`--check` 用于防漂移
- `ShineMusicApi/tools/yt-dlp.exe`：YouTube Music / B 站下载桥接，路径见 `appsettings.json` 的 `SourceDownload:YtDlp`；可选本地 Cookie 不入库，空模板为 `ShineMusicApi/tools/youtube-cookies.example.txt`
- `config/hosts.json`：前后端共用的唯一地址配置（不入库，模板 `config/hosts.example.json` 已入库）
- `docs/`：项目文档，见下方「文档索引」
- `.github/workflows/ci.yml`：CI 三个独立 job —— api（Release 构建且警告即错误 + 单元、架构与真实数据库集成测试）/ app（契约、类型、会话与通知栏回归、i18n、App 构建）/ web（契约、类型、会话回归、构建）；api job 在 `ShineMusicApi/` 下执行

## 本地运行

前置：.NET 10 SDK、Node 22 + yarn、Python 3.12（契约生成器）、Docker Desktop、本机可执行 `ffmpeg` / `ffprobe`。

本地 Compose 基础设施只发布到宿主机 `127.0.0.1`，包括 API 管理任务所用的 RabbitMQ 管理端口；API / Worker 按仓库现有方式直接在宿主机运行，并使用下面的 `.env` 凭据连接本机服务。未来单独部署到服务器时的网络拓扑和对外监听方式尚待另行确定。

首次在仓库根目录运行 PowerShell 脚本 `scripts/New-LocalInfrastructureEnv.ps1`，它会在忽略的 `.env` 中为 PostgreSQL、Redis 和 RabbitMQ 分别生成独立的随机口令（默认 64 个十六进制字符），不显示口令，也不会覆盖已有文件。`.env.example` 只提供变量名模板；不要直接用空模板启动服务。

PostgreSQL 的 `POSTGRES_PASSWORD`、Redis 的 `REDIS_PASSWORD` 和 RabbitMQ 的 `RABBITMQ_DEFAULT_PASS` 均支持自行设置的非空短密码，不要求固定长度或十六进制字符；初始化脚本生成的长密码只是默认值。修改前先停止 API / Worker，修改后在它们各自的 PowerShell 窗口重新加载配置并启动应用。

PostgreSQL 连接字符串会自动处理密码中的分号和引号。Redis 当前使用逗号分隔的连接字符串，`REDIS_PASSWORD` 不支持逗号，加载器会明确提示；普通短密码无需特殊处理。

已有 PostgreSQL 用户需运行 `docker exec -it shinemusic-postgres psql -U shinemusic -d shinemusic`，在 `psql` 中输入 `\password shinemusic`，按提示输入新密码，完成后输入 `\q`，再将同一个值写入根 `.env` 的 `POSTGRES_PASSWORD`。Redis 则将新密码写入 `.env` 的 `REDIS_PASSWORD`，执行 `docker compose --project-directory 'C:\Learn\ShineMusic' up -d --no-deps --force-recreate redis`，让 Redis 重建容器后采用新值并继续使用原数据卷。已有 RabbitMQ 用户需先在本机管理台修改密码，再将同一个值写入 `.env`；仅修改 `.env` 不会更改 PostgreSQL 或 RabbitMQ 已有用户的密码。

Compose 自动读取仓库根目录 `.env`。启动前可用 `docker compose config --quiet` 验证配置；不要用普通的 `docker compose config`，它会显示解析后的值。API 和 Worker 在 `Development` 环境且能定位本仓库时，也会从根 `.env` 读取本地基础设施默认配置；`appsettings.Development.json`、User Secrets、环境变量和命令行的显式配置仍优先。API 的开发启动配置已指定 `Development`；其他环境不会自动读取此文件，可显式配置连接信息，或在各自 PowerShell 窗口先 dot-source `scripts/Load-LocalInfrastructureEnv.ps1`。该脚本将四项 .NET 连接配置注入当前进程及子进程，换终端后需重新加载。不要把口令放入命令参数、命令历史、日志或提交的配置文件。

```powershell
# 首次初始化；已有 .env 时脚本会拒绝覆盖
.\scripts\New-LocalInfrastructureEnv.ps1

# 非 Development 环境或需要显式加载时，在启动应用的同一终端执行
. .\scripts\Load-LocalInfrastructureEnv.ps1
```

```sh
# 1. 配置地址（新机器首次；PowerShell 用 Copy-Item，bash 用 cp）
cp config/hosts.example.json config/hosts.json
# 把 server.origin 改成你的服务器地址；纯本地跑可填 http://127.0.0.1:5180

# 2. 先在 PowerShell 中按上一节生成 .env，然后启动仅限本机访问的基础设施
docker compose config --quiet
docker compose up -d

# 3. 启动本机 API；默认开发启动配置自动读取根 .env
dotnet run --project ShineMusicApi/ShineMusic.Api

# 4. 首次创建管理员：默认开发环境自动读取根 .env；建表后创建账号即退出（不启动 Web 服务）
dotnet run --project ShineMusicApi/ShineMusic.Api -- --create-database --create-admin

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

拆分 Worker 时，在 API 终端执行 `$env:Worker__Mode = 'web'` 后启动 API；在 Worker 的 PowerShell 终端 dot-source `scripts/Load-LocalInfrastructureEnv.ps1` 后再启动 Worker。这样 API 和 Worker 会使用同一组本机 PostgreSQL、Redis 和 RabbitMQ 凭据，同时只由 Worker 运行媒体任务。

### 停止本机 API

若 API 在前台终端运行，在该终端按 `Ctrl+C` 正常退出。若 API 已在后台运行，可在仓库根目录的 PowerShell 中按监听端口停止它：

```powershell
$apiPids = Get-NetTCPConnection -LocalPort 5180 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique

if ($apiPids) {
    $apiPids | ForEach-Object { Stop-Process -Id $_ }
} else {
    Write-Host '本机 API 未运行（5180 端口无监听进程）。'
}
```

拆分 Worker 时，也要在 Worker 的前台终端按 `Ctrl+C`；API 与 Worker 是独立进程，停止 API 不会自动停止 Worker。

媒体任务使用数据库租约：`MediaLease:LeaseSeconds` 默认 120 秒，`HeartbeatSeconds` 默认 15 秒；媒体组在启动时及每 15 秒只恢复租约过期任务，每轮最多恢复 `RecoveryBatchSize`（默认 100）条。Web-only 启动不再全局重置媒体队列。下载和导入每次认领生成新的 `AttemptId`，旧执行失去租约后不能提交结果；临时产物也按 attempt 隔离。

首次升级到租约版本时，先停止所有旧版媒体执行进程（包括 `Worker:Mode=all` 的 API），再启动新版 API/Worker；旧版进程不写租约，不能与新版混跑。两类主机启动都会补齐可空租约列和索引；存量处理中且无租约的任务由新媒体调度器恢复。多 Worker 必须连接同一个业务数据库和 RabbitMQ，并访问同一份隔离/媒体存储；运行节点的系统时间应保持同步。后续新版 Worker 重启后，未完成任务在租约过期并被扫描到后接管，不会立即夺取仍有有效心跳的任务。

如果已有命名卷，`.env` 的新口令不会自动替换 PostgreSQL 数据库角色或 RabbitMQ 用户中已保存的口令。安排维护窗口，先停止 API / Worker，保留并验证旧凭据；先做 PostgreSQL 一致性逻辑备份，再停止三个基础服务，对所有现有命名卷做一致性备份，并在隔离环境验证恢复。记录卷名和挂载关系，确认备份可恢复后才开始轮换。整个过程保留原数据卷，禁止执行 `docker compose down -v`。

首次重建已有 RabbitMQ 容器前，先记录它的 hostname：

```powershell
docker inspect --format '{{.Config.Hostname}}' shinemusic-rabbitmq
```

将此值写入下面生成的 `.env.next` 的 `RABBITMQ_HOSTNAME`，以保持 RabbitMQ 节点名及持久数据目录一致；若原来显式配置了 `RABBITMQ_NODENAME`，也必须保持原值。不要直接给已有节点换名，否则服务可能启动为一个新的空节点。新安装可省略该变量，使用稳定默认名 `shinemusic-rabbitmq`。

安全生成新的临时凭据文件（本机忽略规则也覆盖 `.env.next`），然后补上上述既有 hostname：

```powershell
.\scripts\New-LocalInfrastructureEnv.ps1 -Path .env.next
```

备份验证完成并补齐 `.env.next` 后，保持 API / Worker 停止，先按新配置重建三个服务，收敛旧容器的全部网卡监听，并让 Redis 启用新口令。不要先启动仍绑定全部网卡的旧容器；只重建 Redis 也无法更新 PostgreSQL / RabbitMQ 的旧端口映射。显式指定 `--env-file .env.next`，不依赖根目录 `.env` 是否存在：

```powershell
docker compose --env-file .env.next config --quiet
docker compose --env-file .env.next up -d --force-recreate --no-build --pull never postgres redis rabbitmq
```

确认三个服务健康、仍挂载原命名卷、RabbitMQ 节点名未变，且实际端口绑定仅为 `127.0.0.1`。此时 PostgreSQL / RabbitMQ 数据卷中保存的口令仍是旧值，新环境变量不会覆盖它们；Redis 已采用 `.env.next` 中的新口令。继续保持 API / Worker 停止，通过仅限本机的连接逐一轮换：

1. PostgreSQL：运行 `docker exec -it shinemusic-postgres psql -U shinemusic -d shinemusic` 进入交互终端，在 `psql` 输入 `\password shinemusic`。按提示安全输入 `.env.next` 中新的 `POSTGRES_PASSWORD`（不会在终端回显），完成后输入 `\q`。
2. RabbitMQ：只从本机打开 `http://127.0.0.1:15672`，使用当前账号口令登录；在管理台为 `shinemusic` 用户设置与 `.env.next` 中 `RABBITMQ_DEFAULT_PASS` 相同的新值。
3. 验证 PostgreSQL / RabbitMQ 新凭据可用、旧凭据失效，Redis 新凭据可用且拒绝未认证请求；核对原有数据库、队列和 Redis 数据仍在。只有这些检查通过后，才将 `.env.next` 完整复制为根目录 `.env`，保留其中的三个新口令及既有 `RABBITMQ_HOSTNAME`，并将 `.env` 与临时凭据文件的文件权限限制为当前维护用户和必要的系统管理员可读。若已有 `.env`，先安全保留原凭据供维护回滚；不要打印或提交任何凭据。

运行 `docker compose config --quiet` 确认根目录 `.env` 可用，再在 API / Worker 各自终端重新 dot-source 加载脚本并启动应用，验证应用连接；仅改 `.env` 或重建容器不会更新已运行进程的配置。全部验证完成后删除 `.env.next`，安全保管备份。若 PostgreSQL 或 RabbitMQ 口令更新中断，保持 API / Worker 停止，先核实每个服务当前接受的口令，完成轮换、认证验证与 `.env` 同步后再恢复；不要通过删卷重新初始化来解决。初次部署若命名卷已有数据，也按本节处理。

`--create-admin` 会交互式询问邮箱 / 用户名 / 显示名 / 密码；非交互场景可用环境变量 `ADMIN_EMAIL`、`ADMIN_USERNAME`、`ADMIN_NAME`、`ADMIN_PASSWORD`。库尚未创建时可先加 `--create-database`。

本机默认开发环境可直接使用上面的 `dotnet run` 初始化命令。也可在 PowerShell 运行下面的脚本，显式加载现有根 `.env`，确保数据库存在，然后建表并交互创建管理员，完成后退出，不启动 API/Worker：

```powershell
.\scripts\Initialize-LocalApplication.ps1
# 仅检查/创建数据库本体，不建表或创建管理员：
.\scripts\Initialize-LocalApplication.ps1 -DatabaseOnly
```

如果终端当前位于 `ShineMusicApi/ShineMusic.Api`，可直接执行 `..\..\scripts\Initialize-LocalApplication.ps1`；也可用脚本的绝对路径，从任意目录调用。

也可从仓库根目录在**同一个 PowerShell 窗口**手工依次执行（换窗口后需重新加载）：

```powershell
. .\scripts\Load-LocalInfrastructureEnv.ps1
dotnet run --project .\ShineMusicApi\ShineMusic.Api -- --create-database --create-admin
```

单独的 `--create-database` 只确保数据库本体存在，然后退出，不创建业务表或管理员；组合命令会继续建表、播种角色并询问管理员信息。没有交互终端时，须为当前进程提供完整的 `ADMIN_*`，缺失时会提示并退出。数据库客户端应连接 `127.0.0.1:5432`，数据库与用户名均为 `shinemusic`，密码使用根 `.env` 中的 `POSTGRES_PASSWORD`；这是基础设施凭据，与应用管理员密码不同，重建卷后需更新客户端中保存的旧密码。

| 服务 | 地址 | 说明 |
|---|---|---|
| API | `http://localhost:5180` | `launchSettings.json` 绑定 `0.0.0.0:5180`；`/health` 返回 `{"status":"ok"}`；开发环境另有 `/swagger` |
| Web 后台 dev | `http://localhost:5174` | Vite 只监听 localhost；`/api`、`/health` 走代理 |
| App H5 dev | `http://localhost:5173` | `server.host: true` 绑定 `0.0.0.0`，同 Wi-Fi 可访问；`/api`、`/health` 走代理 |
| 基础设施 | `127.0.0.1:5432` / `:6379` / `:5672` / `:15672` | PostgreSQL / Redis / RabbitMQ AMQP / 管理台，只从本机访问；管理台不开放给局域网 |

> **dev 代理指向 `server.origin`，不是本机 5180**：只要 `hosts.json` 里配了 origin，Web 与 H5 的 `/api`、`/health` 都转发到该地址；origin 留空才回退 `127.0.0.1:5180`。若本机后端与远程不同步，请求会打到远程实例。
> **Web dev 只监听 localhost（IPv6）**，用 `localhost` 而非 `127.0.0.1` 访问更稳。H5 的原生能力（后台播放、通知栏、SAF 选文件）不可用，一律以 HBuilderX 真机为准。

## 提交前必做

本地预检共十一道闸门（契约漂移 / App 类型与会话、通知栏回归 / i18n 残留 / App 构建 / Web 类型与会话回归 / Web 构建 / 后端 Release 构建且警告即错误 / 后端全量测试，含真实 PostgreSQL 集成），与 CI 三个 job 的检查内容对应。完整命令与故障处理记录在本机忽略的 `docs/CI与GitHub提交流程.md` 中，不随仓库提交。

常用命令（在对应前端目录执行）：`yarn check:contracts` / `yarn gen:contracts`、`yarn typecheck`、`yarn test:session` 和 `yarn test:original-audio`（App / Web）、`yarn test:music-notification`（App）、`yarn check:i18n`、`yarn build:app`（App）/ `yarn build:only`（Web）。

本地后端验证从仓库根目录执行，支持 Windows PowerShell 5.1 和 PowerShell 7：

```powershell
powershell.exe -NoProfile -File .\scripts\Test-Backend.ps1 -PostgresPort 45440
```

需要 Docker 已启动、本机已有 `postgres:16-alpine` 镜像，以及可用的 `ffmpeg` / `ffprobe`；`45440` 端口须空闲。脚本完成 Release 构建和全量测试，自动创建并清理独立 PostgreSQL 测试容器，TRX 报告位于 `ShineMusicApi/.artifacts/test-results/`。仅当第⑩道已成功构建当前源码时，第⑪道才可加 `-SkipBuild`；构建失败时应跳过⑪，避免运行旧产物。参数与环境说明见 [后端隔离测试说明](scripts/Test-Backend.README.md)。

CI 的 api job 已提供 `shinemusic_test_ci` 专用测试库、注入 `SHINEMUSIC_TEST_POSTGRES_CONNECTION` 并安装 FFmpeg，因此可直接运行 Release 配置的 `dotnet test --no-build`。本地裸跑测试也必须显式提供测试库连接；本地脚本不会永久修改父终端环境。

## 地址配置

**所有地址只在 `config/hosts.json` 的 `server.origin` 一处维护**：换成自己的服务器时，把这个值改成域名或 `http://服务器IP:端口`，App（dev 与 APK 内置地址一致）、H5 dev 代理、Web 后台 dev 代理与生产直连、后端 CORS 白名单、邀请邮件链接、Swagger 服务器地址全部自动跟随。配置里**不含任何会随 DHCP / 网卡变化的本机局域网 IP**。

| 字段 | 作用 |
|---|---|
| `server.origin` | **唯一服务器地址**（域名或 `http://服务器IP:端口`）。改后重跑 `yarn build:app` 再打 APK；App dev server 监听该文件会自动重启，Web dev server 需手动重启 |
| `local.apiPort` / `local.appApiPort` | 后端端口（默认 5180）；仅在 `origin` 未配置、纯本地开发（无服务器）时作为回退使用 |
| `local.webPort` | Web 前端 dev server 端口（默认 5174，后端据此放行 CORS） |

旧版字段 `local.lanIp` / `apiHost` / `server.webOrigin` / `apiOrigin` / `appApiOrigin` 已从模板移除，代码仅作兼容回退读取；新部署不需要填写它们。若只想纯本地跑（后端就在本机、没有公网入口），把 `origin` 填成 `http://127.0.0.1:5180`，或留空让代码回退到 `127.0.0.1:appApiPort`。

## 下载器本地 Cookie

`ShineMusicApi/tools/youtube-cookies.txt` 仅保存在本机，已加入 Git 忽略规则。仓库提供 Netscape 格式空模板；首次需要该文件时，可在仓库根目录执行以下 PowerShell 命令（已有文件不会被覆盖）：

```powershell
if (-not (Test-Path -LiteralPath 'ShineMusicApi/tools/youtube-cookies.txt')) {
    Copy-Item -LiteralPath 'ShineMusicApi/tools/youtube-cookies.example.txt' -Destination 'ShineMusicApi/tools/youtube-cookies.txt'
}
```

空模板没有任何 Cookie 记录；需要登录态时，由使用者在本机提供对应 Cookie。不要将实际 Cookie 写入模板或提交到仓库。

默认 `SourceDownload:YtDlp:CookiesFile` 为 `../tools/youtube-cookies.txt`，相对路径按后端应用内容根目录解析。也可通过 `SourceDownload__YtDlp__CookiesFile` 指定外部文件的绝对路径；留空表示不传 Cookie，配置文件不存在时记录警告并继续尝试无 Cookie 调用。`SourceDownload:YtDlp:Enabled` 默认关闭，提供 Cookie 不会自动启用音源，也不保证下载成功。

## 原件导出与私有存储

Web 管理后台曲库列表可“下载原件”；App 的本人私有曲目列表也提供下载入口，Android 通过系统保存选择器写入用户指定位置。管理员接口为 `GET /api/v1/admin/tracks/{trackId}/original`（仅公开曲目），本人接口为 `GET /api/v1/me/library/{trackId}/original`（仅本人有效私有曲目）。两者保留原始字节和已有标签，支持 Range/条件请求；历史未标记原件不会自动开放导出。Android 保存流程已做合成原生桥回归，仍须 HBuilderX 真机验收；iOS 原生保存尚未实现。

`LibraryWorkflow:MaxPrivateOriginalBytesPerUser` 默认 `0`，表示不限额；设为正整数则按每用户保留的原件字节限额，支持环境变量 `LibraryWorkflow__MaxPrivateOriginalBytesPerUser`。计量读取实际文件长度并按同用户内容去重，回收站、退休原件和待审快照在释放前仍计入。`GET /api/v1/me/library/storage` 可查询本人占用和剩余量。该限额不包含转码副本及下载隔离区。

HTTP 下载通过不可变前缀快照支持跨 Worker 恢复，强 ETag、资源上下文或前缀校验不满足时从头下载；多 Worker 需要共享数据库和隔离存储。检查点快照总量默认上限 1 GiB、单文件 256 MiB、最长保留 24 小时，不包含硬退出遗留的其他隔离文件。

## 日志输出

操作日志和 `Debug.Request` 仅记录请求路径及有效的正整数 `page` / `pageSize`，其余查询参数（包括媒体 `access_token`、搜索词等）不写入这两类日志；原始请求仍正常传递给业务和鉴权。SQL 执行日志保留参数化语句及参数名，不记录参数值；原始 SQL 中自行拼接的字面量不在此处解析，数据访问仍应使用参数化查询。方法、用户标识、状态码、耗时及操作日志的 traceId 保留，既有历史日志不会因修改自动清理。

## 接口契约与文档

- 契约单一来源：`ShineMusicApi/tools/generate-ts-contracts.py` 从后端 C# `record` 生成 App/Web 两份 `src/types/api.generated.ts`。改动 DTO 后在任一前端执行 `yarn gen:contracts` 并提交两份产物；CI 用 `yarn check:contracts` 防漂移。
- API 历史前缀 `/api/v1`；双栈兼容入口 `/admin/v1/<rest>` → `/api/v1/admin/<rest>`、`/app/v1/<rest>` → `/api/v1/<rest>`，仅在路由前重写路径，鉴权、限流、错误体与 DTO 完全共用；`GET /health` 匿名存活探针；`GET /api/v1/system/capabilities` 返回非敏感能力开关快照。Swagger 仅开发环境在 `/swagger` 暴露。
- 鉴权三级：未标 `[Authorize]` 匿名、`[Authorize]` 需登录、`[Authorize(Roles="admin")]` 仅管理员。`capabilities` 只控制前端入口显隐，**授权始终由 API 执行**。
- **用户上传与曲库审核**：登录用户的上传可保留为私人曲目；疑似重复或质量冲突进入私人审核，用户可查看、确认或提交公开曲库候选。曲目移出个人曲库会进入可恢复回收站，默认保留 7 天；管理员在 `/api/v1/admin/library-candidates` 审核公开候选。候选试听使用受限的临时兼容音频缓存，不改写原始音频资产。
- **媒体消息可靠性**：下载和导入状态更新会在数据库事务中写入 outbox，再由后台派发到 RabbitMQ；死信及派发失败记录可在管理端查看并审计重放，恢复扫描通过租约接管超时任务。管理接口位于 `/api/v1/admin/mq/dead-letters` 及其 `outbox/failed` 子路径。
- **数据字典**：本地 `docs/数据字典.chm` 是 Windows 编译帮助（40 表 / 335 列 / 9 业务域），含目录树、433 条关键字索引、全文查找页与逐列说明；旧的 `.md` / `.csv` / `.xml` / `.xsd` 格式已移除。HTML Help Workshop 的 `hhc.exe` 仅支持 Windows；当前工作区没有字典生成器源码，CI（Ubuntu）也不生成或校验 CHM。实体改名后须先恢复生成器或更新字典来源，再重建 CHM；字典本身不连库、不执行 DDL。

### 随仓库发布的文档

| 文件 | 内容 |
|---|---|
| [Test-Backend.README.md](scripts/Test-Backend.README.md) | 后端隔离测试入口、环境要求、参数与 TRX 报告 |

`docs/CI与GitHub提交流程.md` 以及其他本地资料和生成产物位于 `docs/`，按 `.gitignore` 规则不随仓库发布；部署、功能与 CI 说明以本 README 和上表中的可跟踪文档为准。

## 构建与发布

- **App**：`yarn build:app` → `ShineMusicApp/dist/build/app` → HBuilderX 云打包 → 安装包**仅通过 GitHub Release 分发**，不入仓库。H5 无发版构建。
- **Web 后台**：`yarn build:only` → `ShineMusicWeb/dist`；后端在 dist 存在时同域托管，与 API 同源，无需额外站点或跨域配置。
- **后端**：本地使用上述 `scripts/Test-Backend.ps1` 完成 Release 构建和隔离数据库全量测试；CI 在其独立 PostgreSQL 测试库环境中执行同一套测试。`Directory.Build.props` 已开启 `TreatWarningsAsErrors` + NetAnalyzers，任何新增告警直接失败。测试项目含领域规则、路径安全、双栈路由、后台任务分组、架构约束与真实数据库启动测试。
