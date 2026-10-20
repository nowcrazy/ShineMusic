# 播放免登录修正 Plan

## 问题定位

用户明确三个不可动摇的原则：
1. **播放免登录**（初衷）
2. **上传/审核/解析需令牌**（只有登录用户才能做）
3. **两者不冲突**（免登录用户听正式发布的歌曲）

当前残留问题：
- `frontend/src/services/catalog.ts:53` — `mapTrack` 给所有 stream URL 附加了 query token（`appendAccessToken`），多余
- `frontend/src/pages/home/index.vue:117` — `toLibraryTrack` 同样附加了 query token，多余
- `backend Program.cs OnMessageReceived` — 对 `/stream`、`/preview` 路径提取 query token，这个机制对公共播放已无用（stream 本来就是匿名的）

后端 StreamTrack 实际行为（代码已正确，无需改）：
- 不检查 Track.PublishedAt，不检查 HttpContext.User，只要 MediaAsset.IsPublished 就能返回音频
- 公开曲目和用户私有曲目（MediaAsset.IsPublished=true）的 stream 对任何人都开放（按 ID 播放）
- AnonymousPlayback=true，不拦截匿名用户

## 改动清单

| # | 文件 | 行 | 改什么 |
|---|------|----|--------|
| 1 | `frontend/src/services/catalog.ts` | 53 | `appendAccessToken(resolveUrl(...))` → `resolveUrl(...)` |
| 2 | `frontend/src/pages/home/index.vue` | 117 | `appendAccessToken(resolveUrl(...))` → `resolveUrl(...)` |
| 3 | `backend Program.cs` | 314-332 | 删除 OnMessageReceived 事件处理器（query token 提取） |

## 不动的东西

- `appendAccessToken` 函数本身 — 管理员上传预览（admin-import.ts:237）仍在用它给 `/admin/library/import-items/{id}/preview` 带 token
- PrivateTrackAccess 逻辑 — GetTrack / GetTrackLyrics 对私有曲目的鉴权是正确业务逻辑
- StreamTrack 后端逻辑 — 已经是完全匿名的，行为正确
- AnonymousPlayback feature gate — 保留为配置项

## 验证

1. 前端构建通过
2. 后端编译通过
3. 浏览器 DevTools Network 看 stream 请求 URL 不再包含 `?access_token=`
4. curl 测试：
   - `curl .../api/v1/tracks/{已发布ID}/stream` → 200，无 token
   - `curl .../api/v1/tracks/{私有ID}/stream` → 200，任何人能播（设计意图：上传者可分享链接）
   - `curl .../api/v1/tracks/{已发布ID}` → 200，无 token
