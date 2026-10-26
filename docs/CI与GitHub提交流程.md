# CI 与 GitHub 提交流程（从 CI 检测到 push，照做即可）

> 每步标注执行目录；✅ 成功标志出现即进入下一步；任何失败 → 跳到文末排查表。
> 仓库：`git@github.com:nowcrazy/ShineMusic.git`，分支 `main`。

## 流程总览

| 步骤 | 内容 | 执行目录 |
|---|---|---|
| 1 | 本地预检（七道闸门，与 CI 同源） | 任意（命令内含 cd） |
| 2 | 暂存 | `c:\Learn\ShineMusic` |
| 3 | 提交 | `c:\Learn\ShineMusic` |
| 4 | 推送到 GitHub | `c:\Learn\ShineMusic` |
| 5 | 确认云端 CI 全绿 | 浏览器 |

本地七道闸门对应 GitHub Actions 的三个 job（`.github/workflows/ci.yml`）：

| CI job | 本地闸门 | 说明 |
|---|---|---|
| App 前端 | ①契约 ②类型 ③i18n ④App 构建 | `ShineMusicApp` |
| Web 前端 | ⑤类型 ⑥Web 构建（契约已被①覆盖） | `ShineMusicWeb` |
| 后端 | ⑦构建（警告即错误） | `ShineMusicApi` |

---

## 第 1 步：本地预检（七道闸门）

> ⚠️ **必须在 PowerShell 窗口执行，不能用 cmd**（脚本用了 `;` 分隔与 `$LASTEXITCODE` 语法，cmd 会拆坏且不报明显的错）。
> 当前若是 cmd（提示符 `C:\...>` 无 `PS` 前缀），先输入 `powershell` 回车切换再粘贴；若在 VS Code/Trae 内置终端，确认终端类型是 PowerShell。

执行目录：任意（**整段一次性复制粘贴，不要逐行**）

```powershell
cd c:\Learn\ShineMusic\ShineMusicApp
yarn check:contracts;  if ($LASTEXITCODE -ne 0) { throw "闸门①失败：契约漂移，执行 yarn gen:contracts 后重新检查" }
yarn typecheck;        if ($LASTEXITCODE -ne 0) { throw "闸门②失败：App 类型检查报错，按输出修复" }
yarn check:i18n;       if ($LASTEXITCODE -ne 0) { throw "闸门③失败：新增 UI 中文未走语言包，接线后重查；确属逻辑值则 yarn check:i18n --update 更新基线" }
yarn build:app;        if ($LASTEXITCODE -ne 0) { throw "闸门④失败：App 构建报错，按输出修复" }
cd c:\Learn\ShineMusic\ShineMusicWeb
yarn typecheck;        if ($LASTEXITCODE -ne 0) { throw "闸门⑤失败：Web 类型检查报错，按输出修复" }
yarn build:only;       if ($LASTEXITCODE -ne 0) { throw "闸门⑥失败：Web 构建报错，按输出修复" }
cd c:\Learn\ShineMusic
dotnet build ShineMusicApi/ShineMusic.slnx -c Release --nologo; if ($LASTEXITCODE -ne 0) { throw "闸门⑦失败：后端构建报错/有警告" }
Write-Host "===== [OK] 七道闸门全过，进入第 2 步 =====" -ForegroundColor Green
```

✅ 成功标志：最后一行绿色 `[OK]`，且中途没有任何 throw 中断。

闸门说明：

- **①契约**：`generate-ts-contracts.py --check` 重新生成并与仓库中的 `api.generated.ts` 比对，**一次同时校验 App 与 Web 两份契约**（后端改了 DTO 却没重新生成前端契约时失败）。
- **②⑤类型**：`vue-tsc --noEmit`，App 与 Web 各自跑。
- **③i18n**：扫描 `ShineMusicApp/src` 中的 UI 中文串与基线比对，新增 UI 文案必须进语言包并接线；基线 = 有意保留的逻辑匹配值白名单（`ShineMusicApp/scripts/i18n-residual-baseline.json`）。Web 无此检查。
- **④⑥构建**：App 产 APK 的 web 资源（`yarn build:app`）；Web 产独立站点产物（`yarn build:only`）。
- **⑦后端**：`Directory.Build.props` 已开启 `TreatWarningsAsErrors` + NetAnalyzers，任何告警直接失败。

---

## 第 2 步：暂存

执行目录：`c:\Learn\ShineMusic`

```powershell
git add -A
git status --short
```

✅ 成功标志：输出暂存清单，且其中**没有** `Apk/`、`node_modules/`、`dist/` 字样，即可进入下一步。

---

## 第 3 步：提交

执行目录：`c:\Learn\ShineMusic`

```powershell
git commit -m "feat: 简述本次改动（按实际内容改写）"
```

✅ 成功标志：输出提交摘要（如 `12 files changed, ...`）。

---

## 第 4 步：推送到 GitHub

执行目录：`c:\Learn\ShineMusic`

```powershell
git push origin main
```

✅ 成功标志：输出 `main -> main`，无 `rejected`。

---

## 第 5 步：确认云端 CI

打开 <https://github.com/nowcrazy/ShineMusic/actions>，找到最新提交，等 **后端 / App 前端 / Web 前端** 三个 job 全部绿 ✅。

> 本地七道闸门与 CI 同源，正常直接绿。CI 每次自己 `yarn install --frozen-lockfile`（云端没有 node_modules）；`yarn.lock` 与 `package.json` 不一致会直接失败。

---

## 排查表（任何一步失败时查）

| 现象 | 处理 |
|---|---|
| 闸门① 契约漂移 | `ShineMusicApp` 下执行 `yarn gen:contracts`，提交重新生成的 `src/types/api.generated.ts`（App 与 Web 两份），重新预检 |
| 闸门②/⑤ 类型报错 | 按报错文件/行号修复，重新预检 |
| 闸门③ i18n 残留 | 按输出的文件/文案把新 UI 文案接入语言包（`$t()`/`t()` + key 入语言树）；确属有意保留的逻辑匹配值则 `yarn check:i18n --update` 更新基线后重新预检 |
| 闸门④/⑥ 构建报错 | 按报错修复，重新预检 |
| 闸门⑦ 后端报错/警告 | 清零警告（项目"警告即错误"），重新预检 |
| CI 的 yarn install 失败 | 本地对应目录 `yarn install` → 提交更新后的 `yarn.lock` |
| CI 契约检查失败 | `yarn gen:contracts` → 提交 `api.generated.ts` |
| push 被拒（rejected） | `git pull --rebase origin main` → 解决冲突 → 重新 push |
| 中文文件名显示为 `\346...` | 仅显示问题；`git config core.quotepath false` 可正常显示 |
