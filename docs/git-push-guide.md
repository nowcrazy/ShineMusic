# ShineMusic 首次提交并推送到 GitHub（Windows cmd 版）

> 目标仓库：`https://github.com/nowcrazy/ShineMusic.git`
> **你的终端是 cmd.exe（`C:\Learn\ShineMusic>`），不是 Git Bash**，所以本文所有命令都是 cmd 语法，不会出现 `wc`、`grep`、`rm -rf`、`du` 这类 Unix 命令。
> 所有命令由你自行执行，我不代跑。

---

## 当前进度（19:42 实测）

| 步骤 | 状态 |
| --- | --- |
| `git init` | ✅ 已完成 |
| 身份配置 | ✅ `nowcrazy` / `965610942@qq.com` |
| 首次提交 | ✅ `757412d 初版`，分支 **`main`**，含 **182 个文件** |
| 提交内容体检 | ✅ 无 `bin/`、`obj/`、`node_modules/`、`data/`；`appsettings.json` 的 `Password` 为空；全历史无真实授权码 |
| 关联远端 | ✅ `origin` = `git@github.com:nowcrazy/ShineMusic.git`（SSH 格式） |
| 分支名 | ✅ 已是 `main` |
| 文档改动 | ⚠️ `docs/git-push-guide.md` 有未暂存的修改（我又改了几处），需再 `git add -A` |
| 临时文件 | ✅ `_staged.txt` 已删除 |
| **SSH 公钥** | ❌ **未加到 GitHub —— 这是唯一卡住的地方** |
| 推送 | ❌ 未做 |

**只剩两件事：把公钥加到 GitHub（第 1 节）→ 重新 add + amend → push。**

---

## 0. 先解决你现在遇到的问题

### 问题一（已变化）：`Recv failure: Connection was reset`

**19:37 实测**：`github.com:443` TCP 直接失败，换 4 个已知 GitHub IP 全部超时，不是 DNS 污染。

**19:42 复测**：`github.com:443` **已经能返回 200**，但耗时 10 秒——说明这条通道是**时通时不通**，不是彻底封死。

**结论：443 不稳定，22 端口（SSH）稳定**。两次测试里 `github.com:22` 都是秒连、握手成功。所以推送仍然建议走 SSH，但**你现在可以用浏览器打开 github.com 了**（慢，多等几秒；打不开就刷新或换个时间）。

| 目标 | 19:37 | 19:42 | 结论 |
| --- | --- | --- | --- |
| `github.com:443` | TCP 失败 | 200（10s） | 时通时断，不可靠 |
| `github.com:22` | 握手成功 | 握手成功 | **稳定，选它** |
| `ssh.github.com:443` | 握手成功 | — | 备用通道 |
| `api.github.com:443` | 200 | — | 通 |
| `gitee.com` | 200 | — | 通 |

`Permission denied (publickey)` 的含义是「服务器认出了你，但你还没交出钥匙」—— 也就是说只要把本机公钥挂到 GitHub 账号上，就能推了。

### 问题二：`'wc' 不是内部或外部命令`

`wc` 是 Linux/Git Bash 的命令，cmd 里没有。本文第 4 节全部换成 cmd 原生写法（`findstr`、`git diff --shortstat`），我已经实测过每一条都能跑。

---

## 1. 加 SSH 公钥（唯一卡住的地方）

### 1.1 你本机已经有 SSH 密钥了

我检查过 `C:\Users\96561\.ssh\`，`id_ed25519` 和 `id_ed25519.pub` 都在，**不用重新生成**。

密钥指纹：`SHA256:27ztRx1MNtxbl5iwN2pYe7vy4ik8+SX2m/LzRynTqpE`（加完后在 GitHub 页面上应该看到这串，可以核对）

### 1.2 一条命令把公钥复制到剪贴板

在 cmd 里执行（注意 `<` 后面没有空格）：

```bat
clip < %USERPROFILE%\.ssh\id_ed25519.pub
```

执行完不会有任何提示，但公钥已经在剪贴板里了。

> 如果 `clip` 不可用，就直接用记事本打开 `C:\Users\96561\.ssh\id_ed25519.pub` 手动全选复制。

公钥内容长这样（一整行，不能断行）：

```
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHs2HecShpe6iQnGS8gfFgTHaQz4qmNUuTCLbK7X4wP9 965610942@qq.com
```

### 1.3 粘贴到 GitHub

1. 打开 https://github.com/settings/keys
2. 点 **New SSH key**
3. Title 填 `ShineMusic-本机`（随便写）
4. Key type 保持 **Authentication Key**
5. Key 框里粘贴上面那一整行
6. 点 **Add SSH key**（可能要输一次 GitHub 密码确认）

> **网络提示**：19:42 复测时 `github.com:443` 已经能通（但慢，约 10 秒），浏览器应该能打开。如果页面转圈打不开，就刷新几次；实在不行用**手机浏览器**登录 GitHub 操作。加完一次就永久生效，以后 push 不需要浏览器。

### 1.4 验证 SSH 通了

在 cmd 里执行：

```bat
ssh -T git@github.com
```

- 第一次会问 `Are you sure you want to continue connecting (yes/no)?` → 输入 `yes` 回车
- 成功的话显示：

```
Hi nowcrazy! You've successfully authenticated, but GitHub does not provide shell access.
```

看到 `Hi nowcrazy!` 就说明钥匙配对成功，可以往下走了。

如果还是 `Permission denied (publickey)`，说明公钥没粘全（注意开头 `ssh-ed25519` 和结尾邮箱都要在，中间不能断行）。

---

## 2. 推送前必做：确认密钥没进仓库

之前 `appsettings.json` 里存过真实的 QQ 邮箱 SMTP 授权码，现在已改成空字符串。**这个状态是对的**，但你要确认两件事。

### 2.1 确认配置文件里是空的

打开 `backend\ShineMusic.Api\appsettings.json`，`InvitationEmail` 节里应该是：

```jsonc
"Password": "",          // ← 必须为空
```

不是空的话先改成空再继续。

### 2.2 把授权码写回本机 User Secrets（只影响本机，不进仓库）

```bat
cd backend\ShineMusic.Api
dotnet user-secrets set "InvitationEmail:Password" "你的QQ邮箱SMTP授权码"
dotnet user-secrets list
cd ..\..
```

> 部署到服务器时不用 user-secrets，改用环境变量 `InvitationEmail__Password=xxx`（双下划线）。

### 2.3 不需要重置授权码

你的 `.git` 是全新零提交的，那个授权码从来没被提交出去过，所以**不用去 QQ 邮箱重新生成**。

---

## 3. `.gitignore`：逐行解释 + 建议最终版

### 3.1 现有 12 条规则在挡什么

```gitignore
**/node_modules/     # 前端依赖，几十万个文件
**/dist/             # uni-app / vite 构建产物
**/unpackage/        # HBuilderX 打包输出
**/bin/              # .NET 编译产物（你这里 227 MB）
**/obj/              # .NET 中间产物
**/.vs/              # Visual Studio 缓存
**/.env.local        # 本地环境变量
**/.env.*.local      # 本地环境变量变体
**/.env.app          # 真机调试的地址覆盖文件
*.log                # 任意日志
**/logs/             # 日志目录
/data/               # 根目录 data/：SQLite 库 + 媒体文件 + 隔离区
/.workbuddy-ai/      # AI 助手的工作记录，不随仓库分发
*.db.bak-*           # 数据库备份兜底
```

`**/` 前缀让规则在任意层级生效（`frontend/node_modules/`、`backend/ShineMusic.Api/bin/` 都能命中）；`/data/` 前面只有一个 `/`，表示**只忽略根目录的 `data/`**，这是对的，媒体文件就在那。

### 3.2 建议补充的 6 类

| 要补的 | 为什么 |
| --- | --- |
| `*.db`、`*.db-shm`、`*.db-wal` | 万一数据库被挪出 `data/`，这三件套会一起漏（WAL 文件里是真实数据） |
| `.DS_Store`、`Thumbs.db`、`desktop.ini` | 系统自动生成的垃圾，协作时天天冲突 |
| `**/.idea/`、`*.user`、`*.suo` | Rider / Visual Studio 个人配置，含本机路径 |
| `**/TestResults/`、`**/coverage/` | 跑测试的产物 |
| `**/publish/` | `dotnet publish` 输出，几百 MB |
| `*.pem`、`*.pfx`、`*.key` | 证书私钥，永远不该进仓库 |

### 3.3 建议的最终版（整文件替换）

```gitignore
# ---------- 依赖与构建产物 ----------
**/node_modules/
**/dist/
**/unpackage/
**/bin/
**/obj/
**/publish/
**/TestResults/
**/coverage/

# ---------- IDE / 编辑器个人配置 ----------
**/.vs/
**/.idea/
**/.vscode/*
!**/.vscode/extensions.json
*.user
*.suo

# ---------- 环境变量 ----------
**/.env.local
**/.env.*.local
**/.env.app

# ---------- 日志 ----------
*.log
**/logs/

# ---------- 本地运行数据 ----------
/data/
*.db
*.db-shm
*.db-wal
*.db.bak-*

# ---------- 证书与密钥 ----------
*.pem
*.pfx
*.key

# ---------- 操作系统垃圾文件 ----------
.DS_Store
Thumbs.db
desktop.ini

# ---------- AI 助手的本地工作记录 ----------
/.workbuddy-ai/
```

**必须保留不被忽略的**：`frontend\.env.example` 和 `frontend\.env.app.example`。现有规则只挡 `.env.local` / `.env.app`，这两个示例文件本来就没被挡，是对的——它们是给新同事看的模板。

---

## 4. 命令序列（cmd 版，照抄）

### 4.1 身份已经配好了

我查过你的本地仓库配置：

```
user.name=nowcrazy
user.email=965610942@qq.com
```

已经有了，**不用再配**。如果想设成全局（以后新仓库也生效）：

```bat
git config --global user.name "nowcrazy"
git config --global user.email "965610942@qq.com"
```

### 4.2 让中文文件名正常显示

```bat
git config --global core.quotepath false
```

不加这条，`服务器选型对比.html` 在 `git status` 里会显示成 `\346\234\215\345\212\241...` 八进制转义（文件本身没问题，只是看着痛苦）。

### 4.3 暂存全部文件

```bat
git add -A
```

### 4.4 提交前自检（cmd 版）

> **你已经跑过 `git add -A` 了**，下面是当时的实测结果，你可以对照着复查一遍。

**① 看文件总数**

```bat
git diff --cached --shortstat
```

实测输出：` 2 files changed, 452 insertions(+), 3 deletions(-)`

> 这次只有 2 个文件是正常的——因为「初版」提交已经把另外 180 个文件收进去了。**如果是第一次全新提交（没有 `初版` 那条），这里应该是 150–300 个文件**；几千的话说明 `.gitignore` 没生效，回第 3 节。
> 想确认整条提交（初版 + 这次）一共多少文件，用 `git ls-tree -r --name-only HEAD | find /c /v ""`。

**② 把暂存清单导出成文件**（cmd 下比管道稳，避免 `findstr` 解析问题）

```bat
git diff --cached --name-only > _staged.txt
```

**③ 确认这些目录一个都没进去**（每条都应该**没有任何输出**）

```bat
findstr /I /C:"node_modules" _staged.txt
findstr /I /C:"/bin/" _staged.txt
findstr /I /C:"/obj/" _staged.txt
findstr /I /C:"/dist/" _staged.txt
findstr /I /C:"/unpackage/" _staged.txt
findstr /I /C:"/.vs/" _staged.txt
findstr /I /C:".workbuddy-ai" _staged.txt
```

**④ 确认 `data/` 没进去**（应该没有任何输出）

```bat
findstr /R /C:"^data/" _staged.txt
```

**⑤ 确认 SMTP 密码是空的**

```bat
git grep --cached -n "\"Password\""
```

预期只看到这两处（第二处是本文档自己的示例文本，属于正常）：

```
backend/ShineMusic.Api/appsettings.json:32:    "Password": "",
docs/git-push-guide.md:89:"Password": "",          // ← 必须为空
```

**⑥ 全量扫一遍疑似密钥**（应该没有任何输出）

```bat
git grep --cached -nE "gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,}|BEGIN [A-Z ]*PRIVATE KEY"
```

> 这条正则写成 `gh[pousr]_` 而不是 `ghp_|gho_`，是为了**不让本文档自己被扫出来**——如果把 `ghp_` 这个字面量写进文档，扫描时永远会命中这一行，看着像漏了东西。

**⑦ 收尾，删掉临时文件**

```bat
del _staged.txt
```

> 五条自检全部符合预期再提交。如果哪条不对，用 `git rm -r --cached <路径>` 撤出暂存区，改好 `.gitignore` 再 `git add -A` 重来。

### 4.5 提交

> **你现在的实际状态**：`757412d 初版`（分支 `main`，182 个文件）已经存在且体检干净，`origin` 也已关联好。当前只剩 `docs/git-push-guide.md` 有未暂存的修改（我又改了几处说明文字），所以先重新 add 再并进初版：

```bat
git add -A
git commit --amend --no-edit
```

因为**还没有推送过**，`--amend` 是安全的，最终会是一条干净的首次提交。

如果更想留成两条提交，就用普通提交：

```bat
git commit -m "docs: 补充推送指南与 .gitignore 规则"
```

如果提示 `LF will be replaced by CRLF`，是 Windows 换行符提示，**忽略即可**——它只表示 git 以后检出时会把 LF 转成 CRLF，文件内容不受影响，也不会让提交变脏。

### 4.6 推送（远端和分支都已就绪，只剩这一步）

```bat
git push -u origin main
```

**不需要再 `git remote add`，也不需要再 `git branch -M main`** —— 我查过，`origin` 已经是 `git@github.com:nowcrazy/ShineMusic.git`（SSH 格式），分支也已经是 `main`。

如果哪天要换远端地址：

```bat
git remote set-url origin git@github.com:nowcrazy/ShineMusic.git
```

注意 SSH 格式是 **`git@github.com:nowcrazy/ShineMusic.git`**（中间是冒号，不是斜杠），不是 `https://` 开头。

### 4.7 验证

```bat
git remote -v
git log --oneline -1
git status
```

预期：

- `remote -v` 两行都指向 `git@github.com:nowcrazy/ShineMusic.git`
- `log` 显示你刚提交的那条
- `status` 显示 `Your branch is up to date with 'origin/main'.` 且 `nothing to commit, working tree clean`

---

## 5. 如果 SSH 这条路也走不通（备选方案）

按可能性排序：

### 方案 A：先探远端是不是空的（用 SSH 探）

```bat
git ls-remote git@github.com:nowcrazy/ShineMusic.git
```

- **没有任何输出** → 空仓库，放心推
- **输出一堆 `refs/heads/main`** → 远端已有内容，直接 push 会被拒（`non-fast-forward`）。去 GitHub 网页确认那是不是你建的 README，如果是：

```bat
git remote add origin git@github.com:nowcrazy/ShineMusic.git
git fetch origin
git branch -M main
git pull origin main --allow-unrelated-histories
git push -u origin main
```

### 方案 B：`ssh.github.com` 的 443 端口（实测也通）

如果哪天 `github.com:22` 被封了，改走这个通道。在 `C:\Users\96561\.ssh\config` 里新建文件写入：

```
Host github.com
  HostName ssh.github.com
  Port 443
  User git
```

然后照常 `git push`，git 会自动走 443。

### 方案 C：有代理/VPN 时改回 HTTPS

我检查过，你当前**没有任何代理端口在监听**（7890、7897、10809、1080 等都空）。如果以后开了 Clash / v2ray 之类的客户端，可以给 git 单独配代理（端口按你客户端实际改）：

```bat
git config --global http.proxy http://127.0.0.1:7890
git config --global https.proxy http://127.0.0.1:7890
```

取消代理：

```bat
git config --global --unset http.proxy
git config --global --unset https.proxy
```

### 方案 D：Gitee 中转（实测 gitee.com 200 可达）

如果 GitHub 长期不可达，可以先推到 Gitee，再在 Gitee 后台用「强制同步」拉回 GitHub。这条路能用但多一道手续，不推荐首选。

---

## 6. 常见报错对照表

| 报错 | 原因 | 处理 |
| --- | --- | --- |
| `Recv failure: Connection was reset` | github.com:443 被阻断 | 改 SSH（第 1 节） |
| `'wc' 不是内部或外部命令` | cmd 里没有 Unix 命令 | 用第 4.4 节的 cmd 写法 |
| `Permission denied (publickey)` | 公钥没加到 GitHub，或没粘全 | 重做 1.2，确认整行完整 |
| `Host key verification failed` | 首次连接没确认指纹 | `ssh -T git@github.com` 输 `yes` |
| `remote origin already exists` | 之前加过 origin | `git remote set-url origin git@github.com:nowcrazy/ShineMusic.git` |
| `failed to push some refs` / `non-fast-forward` | 远端已有提交 | 见 5.1 方案 A |
| `src refspec main does not match any` | 还没提交就 push | 先 `git commit`，`git log` 确认有记录 |
| `fatal: not a git repository` | 不在仓库根目录 | `cd /d C:\Learn\ShineMusic` |
| `Filename too long` | Windows 260 字符限制 | `git config --global core.longpaths true` |
| 提交里出现 `bin\Debug\...dll` | `.gitignore` 没生效 | `git rm -r --cached backend\ShineMusic.Api\bin backend\ShineMusic.Api\obj` 后重新 `git add -A` |
| `LF will be replaced by CRLF` | Windows 换行符 | 忽略；想彻底关提示可 `git config --global core.autocrlf true` |

---

## 7. 推送之后：本地服务要重启

有两处改动**必须重启才生效**：

1. **后端**：点赞接口 `DELETE /song-requests/{id}/votes/me` 已下线。旧进程还在跑的话，调删除仍返回 204 而不是 405。
2. **前端**：新增了 `pages/notifications/index` 路由，`pages.json` 的改动要重启 Vite 开发服务器才能加载。

```bat
REM 后端
cd backend\ShineMusic.Api
dotnet run

REM 前端（另开一个窗口）
cd frontend
npm run dev
```

---

## 8. 一句话总结

仓库已经就绪（`757412d 初版` / 182 文件 / 分支 `main` / origin 已关联 / 体检干净），**唯一卡住的是 SSH 公钥还没加到 GitHub**——所以 `ls-remote` 报 `Permission denied (publickey)`。

三步走完就通了：

```bat
REM ① 复制公钥，然后粘到 https://github.com/settings/keys
clip < %USERPROFILE%\.ssh\id_ed25519.pub

REM ② 验证，看到 Hi nowcrazy! 就对了
ssh -T git@github.com

REM ③ 重新 add（文档刚被我改过）+ 并进初版 + 推送
git add -A
git commit --amend --no-edit
git push -u origin main
```
