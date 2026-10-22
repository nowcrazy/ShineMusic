# GitHub CI 处理指南（2026-10-22）

> 适用场景：本次推送包含**前端工程迁移**（`frontend/` → `ShineMusicApp/`），CI 里有两处写死旧路径的脚本已由助手修复。
> 所有 git 提交 / 推送命令均由**你本人**在终端执行，本文档只提供命令、成功标志与故障处理。

---

## 〇、背景：CI 为什么需要处理

CI（`.github/workflows/ci.yml`）在推送 `main` / PR 时自动跑两个 job：

| job | 内容 | 闸门 |
|---|---|---|
| 后端 | dotnet restore + Release 构建 | 警告即错误（TreatWarningsAsErrors） |
| 前端 | yarn install --frozen-lockfile → 契约漂移检查 → vue-tsc 类型检查 → H5 构建 | 任何一步失败即红 |

本次迁移有**两处迁移遗留会直接打红 CI**，已修复：

1. `ci.yml` 前端 job 三处 `frontend/` 路径 → 已改为 `ShineMusicApp/`（工作目录、yarn.lock 缓存路径、构建产物路径）；
2. `backend/tools/generate-ts-contracts.py` 的契约输出路径写死 `frontend/src/types/api.generated.ts` → 已改为 `ShineMusicApp/src/types/api.generated.ts`（未修则 CI 契约检查必失败）。

**本地预检已全部通过**（与 CI 同一命令）：

| 预检项 | 结果 |
|---|---|
| `yarn typecheck`（ShineMusicApp 下） | ✅ 8.9s 通过 |
| `yarn check:contracts`（ShineMusicApp 下） | ✅ 契约一致 |
| `yarn build:h5`（ShineMusicApp 下） | ✅ 37s 构建完成 |
| `dotnet build backend/ShineMusic.slnx -c Release` | ✅ 0 警告 0 错误 |

---

## 一、第 0 步：核对"将被删除"的文件清单（重要，提交前必看）

本次提交除了迁移，还包含一批**工作区里已删除的文件**。先逐个确认是不是你有意删的。

执行目录：`c:\Learn\ShineMusic`

```
git status --short
```

对照下表（`D` 开头的行）：

| 删除项 | 说明 |
|---|---|
| `frontend/**`（整个目录） | ✅ 预期删除——源码已迁至 `ShineMusicApp/` |
| `package.json`（仓库根） | 迁移遗留，根目录不再需要 |
| `dotnet-tools.json` | 迁移遗留 |
| `GitHub提交指南.md` | ⚠️ 上次的操作手册，确认你有意移出仓库 |
| `公网部署指南.md` | ⚠️ 运维手册，确认你本地/别处有留档 |
| `邀请邮件预览.html` | 邮件模板预览页 |
| `.trae/documents/*.md` | AI 助手本地文档 |

**如果其中有误删**，在提交前恢复（不影响其他文件）：

```
git restore "GitHub提交指南.md"
git restore "公网部署指南.md"
git restore "邀请邮件预览.html"
git restore dotnet-tools.json package.json
git restore .trae/documents/
```

确认无误后进入下一步。

---

## 二、第 1 步：暂存与提交

执行目录：`c:\Learn\ShineMusic`（**仓库根目录，不是 ShineMusicApp**）

1) 暂存全部变更：

```
git add -A
```

为什么 `git add -A` 是安全的：`Apk/`（14.6MB 安装包）、`ShineMusicApp/node_modules/`、`ShineMusicApp/dist/`、`ShineMusicApp/unpackage/` 均已被 `.gitignore` 规则排除，不会入库。

2) 核对暂存清单（重点看：新增的应是 `ShineMusicApp/**`，删除的是 `frontend/**`，不应出现任何 `Apk/`、`node_modules/`、`dist/`）：

```
git status --short
```

3) 提交（提交信息可直接复用）：

```
git commit -m "feat: 前端工程迁移至 ShineMusicApp，同步 CI 与契约脚本路径" -m "- frontend/ 全目录移除，源码迁至 ShineMusicApp/（H5 与 App 共用）
- ci.yml 前端 job 路径 frontend -> ShineMusicApp（工作目录/yarn.lock 缓存/构建产物）
- generate-ts-contracts.py 契约输出路径同步迁移
- PlayerBar 横屏双栏布局重做（左封面控制/右歌词面板/右侧抽屉面板）
- notifications 未登录守卫（匿名首页不再请求 unread-count 吃 401）
- Program.cs CORS 白名单追加 null（file:// 场景预留）"
```

成功标志：`git log --oneline -1` 显示新提交；`git status` 只剩干净工作区（或未跟踪的临时文件）。

常见问题：
- **报 `husky`/钩子错误**：本项目未配钩子，如遇请把报错原文发回。
- **中文文件名显示成 `\346\226\207...`**：仅显示问题，不影响提交；想正常显示可执行 `git config core.quotepath false`。

---

## 三、第 2 步：推送到 GitHub

执行目录：`c:\Learn\ShineMusic`

```
git push origin main
```

成功标志：输出类似 `main -> main`，无 `rejected` 字样。

常见问题：
- **`rejected (non-fast-forward)`**：远端有你本地没有的提交，先 `git pull --rebase origin main`，解决冲突后再 push。
- **SSH 认证失败**：`ssh -T git@github.com` 验证密钥。

---

## 四、第 3 步：查看 CI 运行结果

**方式 A（网页）**：打开 `https://github.com/nowcrazy/ShineMusic/actions` → 最新一次提交 → 等「后端」「前端」两个 job 都变绿 ✅。

**方式 B（gh CLI，已安装的话）**：执行目录不限。

```
gh run list --repo nowcrazy/ShineMusic --limit 3
gh run watch --repo nowcrazy/ShineMusic
```

成功标志：`gh run list` 最新一行显示 `completed · success`。

---

## 五、CI 失败排查表（对照 job 名和失败步骤）

| 失败步骤 | 原因 | 处理 |
|---|---|---|
| 前端 / 安装依赖（--frozen-lockfile） | `yarn.lock` 与 `package.json` 不同步 | 本地在 `ShineMusicApp` 下执行 `yarn install`（更新锁文件）→ 重新提交 **yarn.lock** |
| 前端 / 检查契约 | 后端 DTO 改了没重新生成契约 | 本地在 `ShineMusicApp` 下执行 `yarn gen:contracts` → 提交更新后的 `src/types/api.generated.ts` |
| 前端 / 类型检查 | TS 报错 | 本地 `yarn typecheck` 复现 → 修完再推 |
| 前端 / 构建 H5 | 构建期错误 | 本地 `yarn build:h5` 复现 → 按报错修 |
| 后端 / 构建 | 新代码产生警告（警告即错误） | 本地 `dotnet build backend/ShineMusic.slnx -c Release` 复现 → 清除警告 |
| job 整体没跑起来 | workflow YAML 语法错 | 检查 `.github/workflows/ci.yml` 最近改动 |

---

## 附录：本次仓库变更一览（本次提交内容）

**助手修改（已就绪，随本次提交入库）：**
- `.github/workflows/ci.yml`：前端 job 三处路径 `frontend/` → `ShineMusicApp/`
- `backend/tools/generate-ts-contracts.py`：契约输出路径 → `ShineMusicApp/src/types/api.generated.ts`
- `ShineMusicApp/src/components/PlayerBar.vue`：横屏双栏布局重做
- `ShineMusicApp/src/services/notifications.ts`：未登录守卫

**你修改：**
- `.gitignore`：追加 `Apk/` 与 `ShineMusicApp/package-lock.json`（已生效，`git check-ignore` 验证通过）

**后续约定：**
- 前端唯一源码工程为 `ShineMusicApp/`，所有 yarn 命令在其目录下执行
- `Apk/` 下的安装包永不入库；如需分发走 GitHub Release
