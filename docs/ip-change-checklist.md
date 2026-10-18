# IP / 域名变更清单（ShineMusic）

> **一句话：改 IP 只需要改 `config/hosts.json` 一处，然后重启前后端。**
> 前端构建、后端启动都读这一份，不再有重复维护的 IP。

---

## 1. 唯一数据源：`config/hosts.json`

```jsonc
{
  "local": {
    "label": "#Local · 本地开发 / 局域网（npm run dev、真机调试）",
    "lanIp": "192.168.31.145",   // ← 换 IP 只改这里
    "webPort": 5173,
    "apiPort": 5143,
    "appApiPort": 5180,
    "apiHost": "localhost"       // 后端在别的机器时改成 "lan"
  },
  "server": {
    "label": "#Server · 生产部署（npm run build、上线）",
    "webOrigin": "https://music.your-domain.com",
    "apiOrigin": "https://music.your-domain.com"
  }
}
```

`#Local` 与 `#Server` 两段分别对应开发/生产，互不干扰。

---

## 2. 字段含义

| 字段 | 含义 | 被谁使用 |
|---|---|---|
| `local.lanIp` | 当前电脑局域网 IP | 前端 dev 代理（apiHost=lan 时）、App 打包、后端 CORS、邀请邮件链接 |
| `local.webPort` | 前端端口（默认 5173） | 后端 CORS、邀请邮件链接 |
| `local.apiPort` | 后端本机端口（默认 5143） | 前端 dev 代理 |
| `local.appApiPort` | 真机 App 访问的后端端口（默认 5180） | App 打包 |
| `local.apiHost` | `localhost`=后端同机；`lan`=后端在别的机器 | 前端 dev 代理 |
| `server.webOrigin` | 生产前端域名 | 后端 CORS、邀请邮件链接 |
| `server.apiOrigin` | 生产后端域名 | 生产构建（与 webOrigin 相同则走相对路径） |

---

## 3. 换 IP 怎么做（3 步）

1. 改 `config/hosts.json` → `local.lanIp` 为新 IP。
2. 重启后端（让它重新读取 CORS / 邀请链接）。
3. 重启前端 dev（让它重新计算代理目标）。

完成。**不需要**再改 `.env.*`、`appsettings.*.json` 里的任何 IP——那些地方已经不再存 IP。

---

## 4. 各场景

### 场景 A：本机开发（`localhost:5173`）
保持 `apiHost: "localhost"`、`apiPort: 5143`。换 IP 也不用动，因为走 localhost。

### 场景 B：手机 / 另一台电脑访问 H5
1. `hosts.json` 填好 `lanIp`（当前电脑 IP）。
2. 后端用 LAN 配置启动（监听所有网卡）：
   ```bash
   cd backend/ShineMusic.Api
   dotnet run --launch-profile "ShineMusic.Api-LAN (真机 5180)"
   ```
3. 前端监听所有网卡：根目录 `npm run dev`（已带 `--host 0.0.0.0`）。
4. 手机打开 `http://<lanIp>:5173`。
   > 后端 CORS 会自动包含 `http://<lanIp>:5173`，无需手填。

### 场景 C：真机 App 打包
`hosts.json` 的 `lanIp` + `appApiPort` 会自动拼成 `http://<lanIp>:5180`。
直接 `npm run build:app` 即可。

### 场景 D：上线生产
改 `server.webOrigin` / `server.apiOrigin` 为真实域名；
后端以 `ASPNETCORE_ENVIRONMENT=Production` 启动。
> 前后端同域（Nginx 反代 `/api`）时两个字段填一样，前端自动走相对路径。

---

## 5. 验证

```bash
curl http://<lanIp>:5180/health
netstat -ano | findstr :5180
```

浏览器顺序：
1. `http://<lanIp>:5173` 首页能加载
2. 登录不报 CORS
3. 邀请邮件按钮能打开注册页
4. 手机 App 能拉到数据

---

## 6. 排错

| 现象 | 原因 | 处理 |
|---|---|---|
| 登录报 CORS | 后端没重启，仍用旧配置 | 重启后端 |
| 代理 404 / 连不上 | `apiHost`/`apiPort` 不对，或后端没起 | 检查 hosts.json 与后端进程 |
| 改了 hosts.json 不生效 | 前端 dev 未重启 | 重启 `npm run dev` |
| App 请求失败 | `lanIp` 变了但没重新打包 | 改 hosts.json 后 `npm run build:app` |
| 邮件链接打不开 | 后端没重启 / `webPort` 不对 | 重启后端，核对 webPort |

---

## 6.5 在 IDE 中启动（Visual Studio / HBuilderX）

本项目日常是 **VS 跑后端、HBuilderX 跑前端**，触发方式与命令行略有不同：

### 前端（HBuilderX）

1. **首次必须重启一次运行**：停止当前运行 → 重新「运行到浏览器」，让新的 `vite.config.ts`（含 hosts 监听）加载。
2. 之后改 `config/hosts.json` 保存 → 插件自动 `server.restart()`，HBuilderX 控制台可见
   `[hosts] config/hosts.json 已变更，正在重启 dev server…`。
3. 若某版本 HBuilderX 对自动重启支持不佳，直接停止再运行也只需几秒。

> **端口坑（重要）**：HBuilderX「运行到浏览器」的端口不一定等于 `hosts.json` 里的 `webPort`。
> 打开浏览器后看地址栏实际端口，若与 `webPort` 不一致，把 `local.webPort` 改成实际端口，
> 否则后端 CORS 会拦（浏览器报 CORS policy）。
> 也可在 HBuilderX 的 `manifest.json` / 运行配置里把端口固定成 5173。

> 前提：项目是 CLI 工程（有 `package.json` + `vite.config.ts`），HBuilderX 才会走 Vite 并读取本配置。

### 后端（Visual Studio）

1. 改完 `config/hosts.json` 后，VS 里 **Shift+F5 停止 → F5 重新调试**（配置在启动时读取，不会热更新）。
2. VS 的工作目录是项目目录 `backend/ShineMusic.Api`，`../../config/hosts.json` 能正确解析到仓库根目录。
3. 真机调试请选 `ShineMusic.Api-LAN (真机 5180)` profile；本机开发选 `ShineMusic.Api (本机 5143)`。

### 两边的关系

`hosts.json` 是共享文件，但两边都是「启动时读一次」：
- 前端有 watch → 保存即自动重启
- 后端无 watch（CORS 策略启动即固化）→ 必须手动停止再启动

所以标准流程是：**改 hosts.json → 前端自动重启 → VS 里重启后端**。

---

## 7. 设计说明（为什么能"只改一处"）

- **前端**：`vite.config.ts` 用 Node 读 `config/hosts.json`，按 `mode` 算出
  代理目标与 API 基础地址，再通过 Vite `define` 注入常量 `__SM_API_BASE__`；
  `src/services/http.ts` 直接使用该常量。因此无需 `.env` 文件承载 IP。
- **后端**：`Program.cs` 用独立的 `PhysicalFileProvider` 把仓库根目录的
  `config/hosts.json` 挂进配置系统，再按 `ASPNETCORE_ENVIRONMENT` 推导
  `Cors:AllowedOrigins` 与 `InvitationEmail:PublicBaseUrl`（PostConfigure）。
  appsettings 里仍可显式配置作为覆盖，但默认无需填写。
- 两份文件（前端/后端）读的是**同一个物理文件**，所以天然不会不一致。

> 注意：生产发布时若把后端单独拷出仓库，需要保证 `config/hosts.json` 可被找到
> （保持目录结构），或改用环境变量覆盖 `local__lanIp` / `server__webOrigin` 等。

---

## 8. 一劳永逸（可选）

1. 路由器里给电脑绑定静态 IP / DHCP 保留。
2. 用主机名代替 IP：`http://<电脑名>.local:5173`。
3. 内网穿透 / 反代（frp、Tailscale、Cloudflare Tunnel），对外只暴露固定域名。

---

*最后更新：2026-10-18*
