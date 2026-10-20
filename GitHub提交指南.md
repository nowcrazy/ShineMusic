# GitHub 提交指南（含 CI 通关手册）

> 适用：本次「公网部署」改动提交推送到 `github.com:nowcrazy/ShineMusic.git`（main 分支）。
> CI 在每次 push 到 main 时自动运行，**推送前过一遍本文第 3 节的本地自检，基本能保证一次绿**。

---

## 1. 这次提交里有什么

| 文件 | 性质 | 处理 |
|---|---|---|
| `frontend/src/services/http.ts` | 功能：服务器地址可切换（本地设置 > 内置地址） | 提交 |
| `frontend/src/components/ProfileDrawer.vue` | 功能：「我的」抽屉新增服务器地址入口 | 提交 |
| `frontend/vite.config.ts` | 构建：App 模式优先用公网地址 | 提交 |
| `frontend/src/manifest.json` | 配置：正式 AppID + 明文流量许可 | 提交 |
| `frontend/app-icon.png` | 资产：App 图标原图 | 提交 |
| `config/hosts.json` | 配置：公网地址写入 + API 端口统一 5180 | 提交 |
| `backend/ShineMusic.Api/appsettings.Production.json` | 配置：生产监听 0.0.0.0:5180 | 提交 |
| `公网部署指南.md` | 文档：本次部署实录 | 提交 |
| `GitHub提交指南.md` | 文档：本文件 | 提交 |
| ~~`frontend/icon-a.jpg`、`icon-b.jpg`~~ | 中间产物 | 已删除，勿提交 |
| `backend/ShineMusic.Api/ShineMusic.Api.csproj.user` | VS 个人配置 | **取消跟踪**（见第 2 节） |

隐私提醒：`公网部署指南.md` 里写有真实的 `node.tail78b610.ts.net` 地址。若仓库是**公开**的，等于公开了你的服务地址（App 本来也内置它，增量风险小）；介意的话推送前把文档里的地址替换成 `<your-tailnet-host>`。

## 2. 一次性清理：让 csproj.user 永远不再混进提交

`.gitignore` 的 `*.user` 规则**对已被跟踪的文件无效**，必须显式取消跟踪（本地文件会保留，只是不再进 Git）：

```powershell
git rm --cached "backend/ShineMusic.Api/ShineMusic.Api.csproj.user"
```

执行后 `.gitignore` 的 `*.user` 规则立即生效，以后 VS 怎么改启动配置都不会再出现在 `git status` 里。

## 3. 推送前 30 秒自检（对应 CI 的闸门）

在**仓库根目录**的 PowerShell 里跑（前端两条很快）：

```powershell
cd frontend
yarn typecheck      # 对应 CI「类型检查」
yarn build:h5       # 对应 CI「构建 H5」
cd ..
```

- `yarn install --frozen-lockfile`：本次没装新包、没动 yarn.lock，必过
- `yarn check:contracts`：本次没改后端 DTO，必过
- 后端构建：本次只改了 `appsettings.Production.json`（纯 JSON），不会破坏编译

⚠️ **不要在本地跑 `dotnet build`**：命令行构建过后，VS 的热重载 F5 会崩（无堆栈 ExecutionEngineException）。真要本地验证，跑完需删除 `backend\.vs` 文件夹再 F5。交给 CI 验证即可。

自检全绿 → 进第 4 节。

## 4. 提交与推送（照抄即可）

```powershell
# ① 看清要提交什么（确认没有意外文件混入）
git status
git diff

# ② 取消跟踪 csproj.user（第 2 节做过了可跳过）
git rm --cached "backend/ShineMusic.Api/ShineMusic.Api.csproj.user"

# ③ 逐个添加，不要用 git add -A
git add frontend/src/services/http.ts frontend/src/components/ProfileDrawer.vue frontend/vite.config.ts frontend/src/manifest.json frontend/app-icon.png config/hosts.json backend/ShineMusic.Api/appsettings.Production.json "公网部署指南.md" "GitHub提交指南.md"

# ④ 提交（跟随仓库历史风格：类型前缀 + 中文描述）
git commit -m "feat: 公网部署落地——App 内置 Tailscale 固定地址、支持站内切换服务器地址、统一后端端口 5180"

# ⑤ 推送
git push origin main
```

## 5. 盯 CI

推送后自动触发（`.github/workflows/ci.yml`，push 到 main 即跑）。两种看法：

- **网页**：https://github.com/nowcrazy/ShineMusic/actions —— 两个 job：后端（构建+分析器）、前端（契约+类型+构建）
- **命令行**（装了 gh 的话）：

```powershell
gh run watch          # 实时跟踪最新一次运行
gh run view --log-failed   # 失败时看具体日志
```

全绿（约几分钟）= 本次改动在干净环境完整构建通过，收工。

## 6. CI 失败速查表

| 失败的步骤 | 原因 | 处理 |
|---|---|---|
| 安装依赖（frozen-lockfile） | package.json 和 yarn.lock 不同步 | 本地 `yarn install` 更新 lockfile 后连同一起提交 |
| 检查契约 | 改了后端 DTO 没重新生成前端契约 | 本地重跑契约生成命令，提交新的 `api.generated.ts` |
| 类型检查 | TS 报错 | 本地 `yarn typecheck` 复现修复 |
| 构建 H5 | 构建期错误 | 本地 `yarn build:h5` 复现修复 |
| 后端构建 | 新增警告（CI 把警告当错误） | 按日志消除警告，别想办法绕 |

## 7. 三条红线（每次提交都适用）

1. **禁止 `git add -A` / `git add .`**——逐个点名文件，防止 `.env`、个人配置、临时文件混入
2. **禁止 `--no-verify` 绕过钩子**
3. 后端 DTO 有改动时，**先本地 `yarn check:contracts` 再提交**，别让 CI 替你发现
