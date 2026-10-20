# 本地运行与地址配置

## 依赖

- Node.js 18+、Yarn
- .NET 10 SDK（`dotnet tool restore` 后再跑 EF 工具）
- 本机可执行 `ffmpeg` / `ffprobe`（可用 `Ffmpeg__ExecutablePath` 指定绝对路径）
- 开发 JWT：`dotnet user-secrets` 设置 `Auth:SigningKey`（至少 32 字节）；生产用 `Auth__SigningKey`

## 起服务

```sh
# 前端（frontend/ 目录）
cd frontend
yarn install
yarn dev

# 后端（仓库根）
dotnet run --project backend/ShineMusic.Api
```

开发 Swagger：`/swagger`。健康检查：`GET /health`。

SQLite 与媒体默认在仓库根 `data/`（`shinemusic.db`、`library/`、`quarantine/`），已 gitignore。相对连接串会按 API 内容根解析成绝对路径。

首次管理员：

```powershell
dotnet run --project backend/ShineMusic.Api -- --create-admin
```

仅当库中还没有任何用户时允许。也可用 `ADMIN_USERNAME` / `ADMIN_EMAIL` / `ADMIN_PASSWORD` / `ADMIN_NAME` 非交互创建。

## 只改一处的地址：`config/hosts.json`

```json
{
  "local": {
    "lanIp": "192.168.31.145",
    "webPort": 5173,
    "apiPort": 5143,
    "appApiPort": 5180,
    "apiHost": "localhost"
  },
  "server": {
    "webOrigin": "https://music.your-domain.com",
    "apiOrigin": "https://music.your-domain.com"
  }
}
```

| 字段 | 用途 |
|---|---|
| `local.lanIp` | 手机访问电脑、CORS、邀请链接 |
| `local.webPort` | 前端端口 |
| `local.apiPort` | 本机后端；Vite 代理目标 |
| `local.appApiPort` | 真机 App 直连的后端端口 |
| `local.apiHost` | `localhost` 或 `lan`（后端是否在另一台机器） |
| `server.webOrigin` / `apiOrigin` | 生产前端/API；同域可相同 |

改完重启前后端。Vite 会监听该文件并重启 dev server。邀请邮件 `PublicBaseUrl` 未显式配置时，开发环境也会从这里推导。

### 换局域网 IP 检查清单

1. 改 `config/hosts.json` 的 `local.lanIp`
2. 重启 API 与 `yarn dev`
3. 电脑防火墙放行 `apiPort` / `appApiPort` / `webPort`
4. 手机与电脑同一局域网，App/H5 不要用 `localhost` 指电脑
5. 邀请链接若发给手机用户，确认邮件里的主机是局域网 IP 或可达域名
6. 不要把未配 HTTPS 与鉴权的开发服务暴露到公网

## 邮件

`InvitationEmail` 的主机、端口、发件人可放 appsettings；**密码只用** `InvitationEmail__Password` 或 User Secrets。587 一般 `UseSsl: false`（STARTTLS），465 一般 `UseSsl: true`。未配置或投递失败时邀请记 `delivery_failed`，原始令牌不入库、不进响应。

## 上传与转码默认值

- 单文件 ≤ 250 MiB，每批最多 10 个、合计 1 GiB
- 允许：mp3/flac/wav/aac/m4a/m4b/ogg/oga/wma/aiff/ape/mpc/wv/dsf 等
- 转码目标 AAC-LC / M4A 192 kbps，默认并发 1
- Kestrel/表单层上限 4 GiB，真正限制在 Uploads 配置

## 生产注意

- 签名密钥、SMTP、连接串走环境或密钥管理，不提交
- 开发环境密码策略偏松（6 位）；生产应收紧
- OpenAPI/Swagger 仅 Development 暴露
- `data/` 与隔离目录不得放到可静态访问的 Web 根下
