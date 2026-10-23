# HBuilderX 云打包操作流程

> 适用：ShineMusicApp（uni-app CLI 工程）→ Android APK。HBuilderX 版本必须为 5.26 线（当前 5.26.2026091802），与工程内 @dcloudio 依赖同版线，不可混用其他版本打包。

## 1. 前置确认（每次打包前）

```powershell
cd c:\Learn\ShineMusic\ShineMusicApp
```

1. `config/hosts.json` 存在，且 `server.appApiOrigin` 是要内置的公网地址（当前 `https://node.tail78b610.ts.net`）
2. 若本次改过 `config/hosts.json` → 必须重跑第 2 步让地址注入产物
3. 若本次新增/更换过 lucide 图标导入 → 先 `yarn gen:icons`
4. manifest.json 两处不可动：`android.usesCleartextTraffic: true`、`uniStatistics.enabled: false`
5. 原生插件 `XZH-musicNotification` 已配置（见第 6 节），未配置时锁屏/通知栏只显示音频 URL

## 2. 构建 App 产物

```powershell
# 在 ShineMusicApp 目录
yarn build:app
```

✅ 成功标志：生成 `dist/build/app/`（含 `app-service.js`、`app-config-service.js`、manifest）。

打包前验证（不耗云打包次数，可选）：

```powershell
yarn build:h5
```

或 HBuilderX 菜单「运行 → 运行到浏览器」。

## 3. HBuilderX 云打包

1. HBuilderX → 文件 → 打开目录 → 选择 `c:\Learn\ShineMusic\ShineMusicApp`（工程根，不是 src）
2. 确认已登录 HBuilderX 账号（右上角头像）
3. 菜单「发行 → 原生 App-云打包」
4. 打包对话框按以下选择：
   - 平台：**Android**
   - 使用公共测试证书 / 自有证书：与上次打包保持一致（正式分发用自有证书）
   - 打包类型：**正式包**（体验测试包仅自测用）
   - 渠道/广告等选项：默认，勿勾选
5. 点「打包」，等待控制台提示打包完成（正常约 1-3 分钟，实测 16s 出包属正常）

## 4. 产物验收

1. 从控制台输出的下载链接取回 APK
2. 真机安装，检查：启动进首页（若白屏，抓 logcat 看 `[boot-fix]` 行）、曲库点歌能播（公网 API 200）、「我的 → 服务器地址」可打开
3. APK 放 `Apk/` 目录归档（该目录已 gitignore，不入库）

## 5. 分发

- APK **只通过 GitHub Release 分发**，不进 git 仓库
- 发 Release：新建 Release → 上传 APK → 填版本号与更新说明 → 发布
- 用户侧改服务器地址走 App 内「我的 → 服务器地址」，无需重新打包

## 6. 锁屏/通知栏原生插件（XZH-musicNotification）

代码侧已完成适配（`src/services/music-notification.ts` + `player.ts`），插件缺失时自动降级，不影响普通构建。要让锁屏显示歌名/歌手/封面并支持播放控制，需按以下步骤集成：

1. 登录 [插件市场](https://ext.dcloud.net.cn/plugin?id=3476)（用打包同一 DCloud 账号），选择 AppID `__UNI__DC5E049`、包名 `com.shinemusic.app`，0 元购买/绑定插件
2. HBuilderX 打开工程根 `ShineMusicApp` → `manifest.json` → **App原生插件配置** → 选择云端插件 → 勾选 `XZH-musicNotification` → 保存
3. 若用本地插件方式：下载插件 ZIP，解压到工程根 `nativeplugins/` 目录（目录不存在则创建），再在 manifest 里选择本地插件
4. **必须重新云打包**（标准基座不含原生插件）；调试请制作自定义基座
5. 真机首次播放后，Android 13+ 需允许通知权限；小米/华为等还需在系统设置里允许「锁屏显示」「后台弹出界面」

验证点：播放后锁屏应显示歌名、歌手、封面；通知栏/锁屏的上一首/播放暂停/下一首可控制 App；关闭通知栏会停止播放。
