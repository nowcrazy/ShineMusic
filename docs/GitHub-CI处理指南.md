# GitHub 提交流程指南（照做即可）

> 每步标注执行目录；✅ 成功标志出现即进入下一步；任何失败 → 跳到文末排查表。

---

## 第 1 步：本地预检（五道闸门）

> ⚠️ **必须在 PowerShell 窗口执行，不能用 cmd**（脚本用了 `;` 分隔与 `$LASTEXITCODE` 语法，cmd 会全部拆坏且不报明显的错）。
> 当前若是 cmd（提示符 `C:\...>` 无 `PS` 前缀），先输入 `powershell` 回车切换再粘贴；若在 VS Code/Trae 内置终端，确认右上角终端类型是 PowerShell。

执行目录：任意（**整段一次性复制粘贴，不要逐行**）

```powershell
cd c:\Learn\ShineMusic\ShineMusicApp
yarn check:contracts;  if ($LASTEXITCODE -ne 0) { throw "闸门①失败：契约漂移，执行 yarn gen:contracts 后重新检查" }
yarn typecheck;        if ($LASTEXITCODE -ne 0) { throw "闸门②失败：类型检查报错，按输出修复" }
yarn check:i18n;       if ($LASTEXITCODE -ne 0) { throw "闸门③失败：新增 UI 中文未走语言包，接线后重查；确属逻辑值则 yarn check:i18n --update 更新基线" }
yarn build:app;        if ($LASTEXITCODE -ne 0) { throw "闸门④失败：App 构建报错，按输出修复" }
cd c:\Learn\ShineMusic
dotnet build ShineMusicApi/ShineMusic.slnx -c Release --nologo; if ($LASTEXITCODE -ne 0) { throw "闸门⑤失败：后端构建报错/有警告" }
Write-Host "===== [OK] 五道闸门全过，进入第 2 步 =====" -ForegroundColor Green
```

✅ 成功标志：最后一行绿色 `[OK]`，且中途没有任何 throw 中断。

> 闸门③（i18n 残留，2026-10-22 加入）：扫描 src 中的 UI 中文串与基线比对，新增 UI 文案必须进语言包并接线；基线 = 有意保留的逻辑匹配值白名单（`ShineMusicApp/scripts/i18n-residual-baseline.json`），脚本 `check-i18n-residual.mjs`。

---

## 第 2 步：把三个文件移出 git 跟踪（一次性操作，做过则跳过）

执行目录：`c:\Learn\ShineMusic`

```
git rm --cached "Apk/__UNI__DC5E049__20261021175758.apk"
git rm --cached "ShineMusicApp/.hbuilderx/launch.json"
git rm --cached "config/hosts.json"
```

✅ 成功标志：三条各输出一行 `rm '...'`。
（本地文件不会被删；若报 pathspec 不匹配 = 早已移出，直接跳过该条。）

---

## 第 3 步：暂存

执行目录：`c:\Learn\ShineMusic`

```
git add -A
git status --short
```

✅ 成功标志：输出暂存清单，且其中**没有** `Apk/`、`node_modules/`、`dist/` 字样，即可进入下一步。

---

## 第 4 步：提交

执行目录：`c:\Learn\ShineMusic`

```
git commit -m "chore: 仓库瘦身与文档归置——APK/IDE配置/本地地址移出跟踪，md 统一入 docs/，README 重写"
```

✅ 成功标志：输出提交摘要（如 `5 files changed, ...`）。

---

## 第 5 步：推送

执行目录：`c:\Learn\ShineMusic`

```
git push origin main
```

✅ 成功标志：输出 `main -> main`，无 `rejected`。

---

## 第 6 步：确认 CI

打开 <https://github.com/nowcrazy/ShineMusic/actions>，等最新提交的「后端」「前端」两个 job 全部绿 ✅。

> 本地五道闸门与 CI 同源，正常直接绿；CI 多做的一步是云端自己 `yarn install --frozen-lockfile`（仓库里没有 node_modules，它每次自己装）。

---

## 排查表（任何一步失败时查）

| 现象 | 处理 |
|---|---|
| 闸门① 契约漂移 | `ShineMusicApp` 下执行 `yarn gen:contracts`，重新预检 |
| 闸门② 类型报错 | 按报错文件/行号修复，重新预检 |
| 闸门③ i18n 残留 | 按输出的文件/文案把新 UI 文案接入语言包（`$t()`/`t()` + key 入三语树）；确属有意保留的逻辑匹配值则 `yarn check:i18n --update` 更新基线后重新预检 |
| 闸门④ App 构建报错 | 按报错修复，重新预检 |
| 闸门⑤ 后端报错/警告 | 清零警告（项目"警告即错误"），重新预检 |
| CI 的 yarn install 失败 | 本地 `ShineMusicApp` 下 `yarn install` → 提交更新后的 `yarn.lock` |
| CI 契约检查失败 | `yarn gen:contracts` → 提交 `src/types/api.generated.ts` |
| push 被拒（rejected） | `git pull --rebase origin main` → 解决冲突 → 重新 push |
| `rm --cached` 报 pathspec 不匹配 | 该文件已移出跟踪，跳过 |
| 中文文件名显示为 `\346...` | 仅显示问题；`git config core.quotepath false` 可正常显示 |
