# 点歌需求 (Song Request) 状态机与权限设计

> 适用项目：ShineMusic  
> 涉及模块：后端 `ShineMusic.Api/Endpoints/SongRequestEndpoints.cs`  
> 涉及实体：`SongRequestRecord` / `SongRequestVote` / `AdminAuditEntry`  
> 文档版本：v1.0 · 2026-10-18

## 0. 你的问题清单

| 编号 | 你提的问题 |
|------|------------|
| Q1 | 用户自己提的需求，能不能自己删除？ |
| Q2 | 发布到系统上了，管理员是否可以操作？ |
| Q3 | 我发布后，该如何归档？ |
| Q4 | 假设我不发布，该如何拒绝或记录？ |
| Q5 | 被拒绝/归档后，是否还能再起（被开启）？ |

## 1. 现状盘点（基于 `SongRequestEndpoints.cs` + `IdentityEntities.cs`）

| 已实现 | 详情 |
|--------|------|
| 5 个状态 | `open` / `planned` / `in_progress` / `fulfilled` / `declined` |
| 防重复 | 同一规范化 Title+Artist+Album，非 declined 不可重复 |
| 投票关闭 | `fulfilled` / `declined` 后不可再投票 |
| 上架绑定 | `fulfilled` 必须带 `MatchedTrackId`（关联已上架曲目） |
| 审计 | `AdminAudit.Add(...)` 记录 admin 的状态更新 |
| 管理端 | 列表 `GET /admin/song-requests` + 修改 `PATCH /admin/song-requests/{id}` |
| 我的列表 | `GET /me/song-requests`（我提交的 + 我投票的） |

| 缺失（对应你提的问题） | 影响 |
|------------------------|------|
| ❌ 用户撤回/自删除端点 | 提交人想反悔只能联系管理员 |
| ❌ 状态流转校验 | 管理员可任意跳状态（如 `fulfilled` → `open`），数据不严谨 |
| ❌ 归档语义不明确 | `fulfilled` 是终态，但未规定多久后从公共列表隐藏 |
| ❌ 拒绝通知 | 提交人不知道被拒绝 |
| ❌ 再开启机制 | 仅能"新提一条"（CreateRequest 不算 declined 为重复），但原记录无任何复活路径 |

## 2. 推荐完整状态机

### 2.1 状态总览

```
终态（不能再流转，保留留痕）：fulfilled / declined / withdrawn
可流转（中间态）：open / planned / in_progress
```

> 在现有 5 个状态基础上**新增 `withdrawn`（用户/管理员撤回）**；归档通过 `fulfilled` + 自动隐藏实现，不引入新状态。

### 2.2 状态流转图

```
                        (用户提交)
                            │
                            ▼
                       ┌────────┐  ①user.withdraw   ┌──────────┐
                       │  open  │ ─────────────────▶│ withdrawn│
                       └────────┘                    └──────────┘
                          │ │                              ▲
        ②admin.plan       │ │                              │ ③admin.withdraw
                          │ │ ④admin.decline               │ (仅在 planned 且未启动)
                          ▼ ▼                              │
                       ┌────────┐                    ┌─────┴────┐
                       │planned │                    │ planned  │
                       └────────┘                    └──────────┘
                          │                                
        ⑤admin.start     │                                
                          ▼                                
                  ┌─────────────┐                         
                  │ in_progress │                         
                  └─────────────┘                         
                  │           │                          
   ⑥admin.fulfill │           │ ⑦admin.decline (无法寻源) 
   (带 MatchedTrackId)         │                          
                  ▼           ▼                          
             ┌─────────┐  ┌─────────┐                   
             │fulfilled│  │declined │                   
             └─────────┘  └─────────┘                   
                  │           │                          
   ⑧auto-archive │           │ ⑨admin.reopen / ⑩user.resubmit
   (N天后沉底,不隐藏)          │                          
                  ▼           ▼                          
             (历史保留)   (历史保留)                    
```

### 2.3 状态流转表（合法迁移）

| From | To | 触发者 | 必备条件 | 审计 |
|------|----|----|----------|------|
| (无) | open | 用户（创建） | 防重通过 | 可选 |
| open | **withdrawn** | 提交人 | 仅本人 + 状态=open | 写 UserActionLog |
| open | planned | 管理员 | — | AdminAudit |
| open | declined | 管理员 | AdminNote 必填 | AdminAudit |
| planned | in_progress | 管理员 | — | AdminAudit |
| planned | declined | 管理员 | AdminNote 必填 | AdminAudit |
| planned | **withdrawn** | 管理员 | 未启动处理 | AdminAudit |
| in_progress | fulfilled | 管理员 | **MatchedTrackId 必填** | AdminAudit |
| in_progress | declined | 管理员 | AdminNote 必填 | AdminAudit |
| declined | open | 管理员（reopen） | ReopenNote 必填 | AdminAudit + 通知原提交人 |
| fulfilled | *（不可流转）* | — | 终态保留 | — |
| declined | *（除 reopen 外不可流转）* | — | 终态 | — |
| withdrawn | *（不可流转）* | — | 终态 | — |

> **任何不在上表的迁移**（如 `fulfilled` → `open`、`in_progress` → `open`）应直接 400 拒绝。  
> **实现说明**：为兼容管理端"直达操作"，实现中允许**向前跳级**（如 `open → fulfilled`、`planned → fulfilled`），但仍严格禁止回退与终态再流转。

## 3. 权限矩阵

| 操作 | 匿名 | 普通用户 | 提交人 | 管理员 |
|------|------|----------|--------|--------|
| 浏览公共池（默认只看 open/planned/in_progress） | ✅ | ✅ | ✅ | ✅ |
| 提交新需求 | ❌ | ✅ | ✅ | ✅ |
| 点赞（一人一次，**不可取消**） | ❌ | ✅（仅 open/planned/in_progress） | ✅ | ✅ |
| 查看自己的需求（含 declined/fulfilled） | ❌ | ❌ | ✅（`/me/song-requests`） | ✅ |
| **撤回自己的需求** | ❌ | ❌ | ✅ **仅 status=open** | ✅ |
| 修改状态 | ❌ | ❌ | ❌ | ✅ 按流转表 |
| 写 AdminNote | ❌ | ❌ | ❌ | ✅ |
| 关联 MatchedTrackId（fulfill） | ❌ | ❌ | ❌ | ✅ |
| **Reopen declined** | ❌ | ❌ | ❌ | ✅（`POST /admin/song-requests/{id}/reopen`，原因必填） |
| **硬删除** | ❌ | ❌ | ❌ | ❌（仅超管 + 审计） |
| 查看审计日志 | ❌ | ❌ | ❌ | ✅ |

## 4. 关键场景处理（直接对应你 5 个问题）

### Q1 · 用户自己提的需求能不能删除？
**答：能"撤回"，不能"硬删"。**  
- 提交人 `DELETE /api/v1/song-requests/{id}`（或前端"撤回"按钮），仅当 `status == open` 时成功 → 状态置为 `withdrawn`，写 `UserActionLog`，投票保留（不删）。  
- 一旦管理员已处理（`planned`+），提交人无权再撤回，需联系管理员；管理员可代为 `withdrawn`。  
- **不做硬删**，所有记录保留留痕，利于追溯与防纠纷。

### Q2 · 发布到系统上后，管理员是否可以操作？
**答：可以，但有限制。**  
- `fulfilled` 是终态，**不允许再流转**（防止已上架曲目的需求被偷偷改回）。  
- 唯一例外：如需纠错（例如关联合错曲目），由超管做硬删后让用户重新提交（极少见，应走客服流程）。  
- 在 `in_progress` 阶段管理员可自由流转。  
- 所有 admin 操作必须落 `AdminAuditEntry`（已具备 ✅），可追溯。

### Q3 · 发布后该如何归档？
**答：保留但沉底，不隐藏。**  
- **逻辑归档**：`fulfilled` 本身就是终态，**保留**所有字段（Title/Artist/Votes/AdminNote/MatchedTrackId/时间线）作为历史。  
- **视图归档**：发布 **N 天后**（默认 30 天，`SongRequests:ArchiveAfterDays` 可配）由后台任务写入 `ArchivedAtUtc`；公共池**仍然展示**，但排到列表**底部**（进行中 > 已满足 > 已归档）。  
- **不引入新的 `archived` 状态**，避免状态机膨胀；归档只是排序信号，不是状态变更。  
- 提交人始终可在「我的」看到自己的全部历史。

### Q4 · 不发布该如何拒绝/记录？
**答：状态 → `declined` + 强制写理由 + 审计 + 通知。**  
- 管理员 `PATCH` 切到 `declined` 时，`AdminNote` **必填**（限 2000 字符，前端做必填校验）。  
- `AdminAuditEntry` 记录：操作人、UTC 时间、从→到、`AdminNote` 摘要。  
- 系统给**原提交人发通知**（站内信/推送/邮件，三选一），文案模板：「你的点歌需求《{title}》已被管理员拒绝，原因：{adminNote}。可重新提交或联系管理员。」  
- 投票停止（已有 ✅）。  
- 列表/详情对提交人本人仍可见 declined 状态，其他人看不到（默认查询过滤 `status != declined`）。

### Q5 · 被拒绝/归档后，是否还能再起？
**答：能，两条路径，两条都已落地。**

| 方案 | 谁发起 | 行为 | 状态 |
|------|--------|------|------|
| **Resubmit（重新提交）** | 用户 | 在"我的"看到 declined/withdrawn 记录，点"重新提交"→ 创建一条**新** `SongRequestRecord` | ✅ 已实现 |
| **Reopen（重新开启）** | 管理员 | `POST /admin/song-requests/{id}/reopen`，`declined → open`，原因必填，原投票自动恢复 | ✅ 已实现 |

**两条路径的分工（重要）**
- **resubmit 归用户**：用户自己放弃过的（withdrawn）或想再争取一次的，走新记录，投票从 0 开始，历史互不干扰。
- **reopen 归管理员**：当时因缺音源/版权拒绝，现在补到了 → 直接复活原记录，**保留原有票数**，避免"热门需求被清零"。
- reopen 会清空 `AdminNote`（当前状态不再有拒绝原因），原拒绝原因保留在 `SongRequestEventRecord.Note` 里，随时可查。

**防刷**：同一 (Title, Artist, Album) 存在未结束记录时，`CreateRequest` 直接 409；`withdrawn`/`declined` 不计入查重，所以 resubmit 不会被自己挡住。

## 5. 数据模型（已实现）

```csharp
// SongRequestRecord 新增字段
public DateTime? ArchivedAtUtc { get; set; }     // 自动归档时间（视图层沉底，不隐藏）

// 新增：需求状态事件（时间线数据源，append-only）
public sealed class SongRequestEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public string Kind { get; set; }          // submitted/planned/in_progress/fulfilled/declined/withdrawn/reopened
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public string ActorKind { get; set; }     // user / admin / system
    public string? Note { get; set; }         // 拒绝原因 / 重新开启原因
    public DateTime CreatedAtUtc { get; set; }
}

// 新增：站内信
public sealed class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Kind { get; set; }          // song_request.declined / fulfilled / reopened / withdrawn ...
    public string Title { get; set; }
    public string? Body { get; set; }
    public string? LinkUrl { get; set; }      // 点击跳转（默认需求池）
    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public DateTime? ReadAtUtc { get; set; }  // 为空即未读
    public DateTime CreatedAtUtc { get; set; }
}
```

> 事件表取代了原设计的 `UserActionLog`：用户自助动作（提交/撤回）与管理员动作共用一条时间线，天然按需求聚合，不必再按用户维度二次关联。
> `withdrawn` 复用现有 `Status` 字段（值加进 `SongRequestStatuses.All`），不新增独立表。
> 迁移：`20261018105119_AddNotificationsAndSongRequestEvents`。

## 6. API（已实现）

| 方法 | 路径 | 权限 | 说明 |
|------|------|------|------|
| DELETE | `/api/v1/song-requests/{id}` | 提交人（仅 open）/ 管理员（open、planned） | 自撤回，置 withdrawn |
| GET | `/api/v1/song-requests/{id}/timeline` | 匿名可读 | 返回状态时间线；`note` 仅提交人本人与管理员可见 |
| POST | `/api/v1/admin/song-requests/{id}/reopen` | 管理员 | `declined → open`，`reason` 必填，写审计 + 通知 |
| GET | `/api/v1/me/notifications` | 登录用户 | 消息列表（`unreadOnly`、分页） |
| GET | `/api/v1/me/notifications/unread-count` | 登录用户 | 未读数（角标） |
| POST | `/api/v1/me/notifications/{id}/read` | 登录用户 | 单条已读（幂等） |
| POST | `/api/v1/me/notifications/read-all` | 登录用户 | 全部已读 |
| DELETE | `/api/v1/me/notifications/{id}` | 登录用户 | 删除消息 |
| GET | `/admin/song-requests?status=&sort=` | 管理员 | 需求队列（含 declined/withdrawn 终态） |

> 未做 resubmit 专用端点：前端直接用 `POST /song-requests` 复制内容新建，查重已排除 declined/withdrawn，无需服务端特判。

## 7. 状态流转校验实现要点

在 `SongRequestEndpoints.UpdateRequest` 入口加校验：

```csharp
// 伪代码：合法迁移白名单
private static readonly Dictionary<string, HashSet<string>> AllowedTransitions = new()
{
    [SongRequestStatuses.Open]        = [SongRequestStatuses.Planned, SongRequestStatuses.Declined, SongRequestStatuses.Withdrawn],
    [SongRequestStatuses.Planned]     = [SongRequestStatuses.InProgress, SongRequestStatuses.Declined, SongRequestStatuses.Withdrawn],
    [SongRequestStatuses.InProgress]  = [SongRequestStatuses.Fulfilled, SongRequestStatuses.Declined],
    [SongRequestStatuses.Fulfilled]   = [],  // 终态
    [SongRequestStatuses.Declined]    = [SongRequestStatuses.Open], // 仅 V2 reopen
    [SongRequestStatuses.Withdrawn]   = [],  // 终态
};

if (!AllowedTransitions.TryGetValue(request.Status, out var allowed) || !allowed.Contains(newStatus))
    return ValidationError(ctx, "status", $"Cannot transition from {request.Status} to {newStatus}.");
```

## 8. 通知文案（已实现）

写入位置：`NotificationWriter.Push`，由 `UpdateRequest` / `ReopenRequest` / `WithdrawRequest` / 发布入库四处触发。

| 事件 | 标题 | 正文 |
|------|------|------|
| → declined | 需求《{title}》暂不收录 | 原因：{adminNote} |
| → fulfilled | 你想要的《{title}》已入库 | 现在可以在曲库里找到并播放了。 |
| reopen | 需求《{title}》已重新开启 | 管理员重新受理了这条需求：{reason} |
| 管理员代撤回 | 需求《{title}》已被管理员撤回 | 如需继续，可在需求池重新提交。 |

> `planned` / `in_progress` **不发站内信**：这类中间进度在需求卡片的「处理进度」时间线里可见，避免刷屏。若后续想提醒，可在 `PushStatusNotification` 的 switch 里加两个 case。

## 9. 待办与 V2 项

- [x] V1：加 `withdrawn` 状态 + 自撤回端点 + 状态流转校验
- [x] V1：默认查询过滤 declined / withdrawn（公共池不可见，自己的可见）
- [x] V1：declined 强制填写 AdminNote（拒绝原因）
- [x] V1：`SongRequestDto` 增加 `CreatedByMe`，前端据此显示「撤回/重新提交」
- [x] V1：前端「我的」tab 撤回（二次确认）+ 重新提交（复用 `createSongRequest`，查重已排除 `withdrawn`）
- [x] V1：declined / fulfilled / reopen 通知提交人（站内信 + 未读角标）
- [x] V1：自动归档 Job（`SongRequestArchiveService`，fulfilled 超过 `SongRequests:ArchiveAfterDays` 天写 `ArchivedAtUtc`，公共池**沉底不隐藏**）
- [x] V1：admin reopen 端点（`POST /admin/song-requests/{id}/reopen`）
- [x] V1：状态时间线（`SongRequestEventRecord` + `GET /song-requests/{id}/timeline` + 卡片展开）
- [ ] V2：批量管理（按歌曲名合并相似需求）
- [ ] V2：站内信清理策略（如仅保留 90 天，或已读 30 天后自动删除）

---

**附：与现有代码的差异一览**

| 现状 | 设计后 | 改动点 |
|------|--------|--------|
| 5 个状态 | 6 个状态（+withdrawn） | `SongRequestStatuses` 加常量 |
| 状态可任意改 | 严格白名单 | `UpdateRequest` 加校验 |
| 用户无撤回端点 | 自撤回 | 新增 `DELETE /song-requests/{id}` |
| 仅 AdminAudit | + UserActionLog | 新表 + DbContext 注册 |
| 无归档 | ArchivedAtUtc 字段 + Job | 新字段 + 后台任务 |
| 无 resubmit 链路 | ResubmittedFromId/ToId | 新字段 + 端点 |
| declined 默认公共可见 | 默认隐藏 | 查询过滤 |
