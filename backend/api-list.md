# Shine Music API 清单草案

基础路径：`/api/v1`。本清单按当前已确认的目标编写：个位数用户、局域网中的单台服务器曲库、不接第三方音乐平台。

当前已实现健康/能力查询、首页和曲库浏览、封面与 HTTP Range 播放、邀请认证/管理员邀请管理、收藏歌单、歌曲需求/投票、管理员隔离导入/元数据审核/AAC 转码/发布，以及管理员曲库和精选维护。收藏歌单、歌曲需求、隔离导入和编辑精选受服务端功能开关约束；关闭的功能路由返回统一 `404`，匿名播放关闭时仅要求播放请求认证。管理员写操作写入审计记录。

字段、枚举、错误格式和全局开关见 [domain-contract.md](domain-contract.md)。

## 首版必需：健康检查与曲库浏览

- `GET /health`：服务健康检查。
- `GET /system/capabilities`：读取客户端可见的非敏感功能开关、格式白名单和上传限制。
- `GET /music/home`：返回编辑精选、最近加入等首页内容；首版不依赖个性化推荐。
- `GET /tracks?q={关键词}`：搜索歌曲、艺人、专辑。
- `GET /tracks`：曲目列表；支持 `artistId`、`albumId`、`genre`、`sort`、`page`、`pageSize` 筛选和分页。
- `GET /tracks/{trackId}`：曲目详情，包含艺人、专辑、封面、时长、格式等元数据。
- `GET /tracks/{trackId}/stream`：播放音频文件；支持 HTTP `Range`，客户端可拖动进度。
- `GET /albums`：专辑列表，支持分页和搜索。
- `GET /albums/{albumId}`：专辑详情及曲目列表。
- `GET /artists`：艺人列表，支持分页和搜索。
- `GET /artists/{artistId}`：艺人详情及专辑/曲目列表。
- `GET /genres`：曲库中实际存在的风格或分类。
- `GET /artwork/{artworkId}`：读取已发布曲目或专辑的封面。
- `GET /collections`：读取编辑精选专题列表。
- `GET /collections/{collectionId}`：读取专题及其中的曲目。

以上曲库浏览和播放允许匿名访问。`GET /tracks/{trackId}/stream` 必须支持 HTTP `Range`，以便播放器拖动进度。

## 首版必需：用户认证

- `POST /auth/invitations/validate`：验证邀请令牌是否有效，并返回脱敏邮箱和过期时间。
- `POST /auth/register`：提交邀请令牌、密码和显示名称，兑换邀请并创建账号；无有效邀请令牌时拒绝注册。
- `POST /auth/login`：账号登录，返回访问凭证。
- `POST /auth/refresh`：刷新访问凭证。
- `POST /auth/logout`：退出并撤销刷新凭证。
- `GET /me`：读取当前登录用户资料。

匿名用户可以浏览和听歌；收藏、歌单和歌曲需求接口要求登录。管理接口还需要管理员角色。公开注册关闭，任何新账号都必须由管理员邀请。

### 管理员邀请接口

- `POST /admin/invitations`：提交受邀邮箱，创建一次性邀请并向该邮箱发送注册链接。
- `GET /admin/invitations?status=pending`：查看邀请及投递状态。
- `POST /admin/invitations/{invitationId}/resend`：重新发送邀请；旧链接作废并签发新令牌。
- `DELETE /admin/invitations/{invitationId}`：撤销尚未兑换的邀请。

邀请令牌应绑定目标邮箱、限时、单次使用；服务端只保存令牌哈希，不在管理接口响应中返回原始令牌。注册链接打开前端注册页，令牌用于验证并兑换。建议邀请有效期默认为 48 小时，最终可配置。首个管理员账号通过服务器本机初始化创建，不开放公网管理员注册。

## 首版必需：歌曲需求池

用户需要登录才能提交需求或投票。用户发现歌曲已在曲库时，搜索结果标记“已在曲库”，不重复创建需求；用户没有删除曲目的权限。

- `GET /song-requests`：歌曲需求列表，支持按 `votes`、`createdAt` 排序和分页；匿名可浏览。
- `POST /song-requests`：登录用户提交需求，字段包括歌曲名、艺人、专辑（可选）和备注（可选）。服务端检查是否已有匹配曲目或重复需求。
- `GET /song-requests/{requestId}`：需求详情和当前状态。
- `GET /song-requests/{requestId}/timeline`：需求处理时间线（按时间正序返回状态事件：`submitted`/`planned`/`in_progress`/`fulfilled`/`declined`/`withdrawn`/`reopened`）。匿名可访问；事件备注（拒绝原因、重新开启原因）仅对提交人本人与管理员返回。
- `GET /me/song-requests`：当前用户创建或投过票的需求。
- `PUT /song-requests/{requestId}/votes/me`：为需求点赞；每个用户每个需求最多一票，重复调用幂等（返回 204，不重复计数）。**点赞不可取消**：不提供取消投票端点，前端重复点击只提示「已点过赞」。
- `DELETE /song-requests/{requestId}/votes/me`：~~取消投票~~ **已下线**（产品规则：点赞不可撤销），调用返回 405。
- `DELETE /song-requests/{requestId}`：撤回需求（软撤回，置 `withdrawn` 终态）。提交人仅可在 `open` 阶段撤回；管理员可在 `open`/`planned` 阶段代为撤回；已结束（`fulfilled`/`declined`/`withdrawn`）不可撤回。
- `GET /admin/song-requests?status=open&sort=votes`：管理员查看需求队列，默认按有效投票数降序。
- `PATCH /admin/song-requests/{requestId}`：管理需求状态或说明；只有管理员可修改。状态流转受白名单约束（允许向前推进/跳级，禁止回退与终态再流转）；切到 `declined` 时必须填写 `adminNote` 作为拒绝原因；`fulfilled` 时必须关联已发布曲目且通过标题/艺人匹配校验。未传 `adminNote` 时保留原备注，不会被清空。
- `POST /admin/song-requests/{requestId}/reopen`：重新开启已拒绝的需求（`declined → open`），请求体 `{ "reason": "..." }` 必填；原因写入时间线、写审计，并给提交人发站内信。仅 `declined` 状态可重新开启。

歌曲需求状态：`open`、`planned`、`in_progress`、`fulfilled`、`declined`、`withdrawn`。公共需求池默认不展示 `declined`/`withdrawn`，已满足/已归档的需求保留但排到列表底部；提交人可通过 `/me/song-requests` 查看自己的全部记录。用户投票数用于确定优先级；管理员仍可结合版权、音源质量和处理成本决定是否安排。

### 站内信

需求状态变化会向提交人投递站内信（拒绝/入库/被管理员撤回/重新开启），驱动顶部未读角标与消息列表。

- `GET /me/notifications?unreadOnly=false&page=1&pageSize=20`：当前用户的消息列表，按时间倒序。
- `GET /me/notifications/unread-count`：未读消息数，返回 `{ "count": n }`，供角标轮询。
- `POST /me/notifications/{notificationId}/read`：标记单条已读（幂等）。
- `POST /me/notifications/read-all`：全部标记已读。
- `DELETE /me/notifications/{notificationId}`：删除单条消息。

消息类型（`kind`）：`song_request.declined`、`song_request.fulfilled`、`song_request.reopened`、`song_request.withdrawn`、`song_request.progress`、`system`。所有端点仅作用于当前登录用户，他人的消息一律按 404 处理。

## 首版必需：管理员批量导入、自动转码与发布

只有管理员可以上传音频。管理入口放在同一 Vue 项目中，按 `admin` 角色显示。批量文件进入隔离导入区，系统逐文件校验、解析和转码；管理员检查并修正元数据后才能发布。可将导入项关联到歌曲需求，发布后自动将该需求标记为 `fulfilled`。

- `POST /admin/library/imports`：以 `multipart/form-data` 上传一个或多个音频文件，可选关联 `songRequestId`；返回批次和逐文件导入项 ID。此接口仅管理员可用。
- `GET /admin/library/imports/{importId}`：查询批次及各文件处理状态。
- `GET /admin/library/import-items/{itemId}`：查看单文件解析、转码结果和元数据。
- 导入项响应新增 `songRequestMatch` 字段（`kind`/`trackDisplay`/`requestDisplay`/`blocking`/`message`）：关联需求后，元数据一解析完成即返回标题/艺人匹配结论，前端可在列表上提前标黄/标红，不必等发布才发现传错歌；未关联或元数据未解析时为 `null`。
- `GET /admin/library/import-items/{itemId}/preview`：管理员试听隔离区文件或兼容预览版本，支持 HTTP `Range`。
- `PATCH /admin/library/import-items/{itemId}`：修正标题、艺人、专辑、时长、年份、曲目序号、风格，或关联歌曲需求。
- `POST /admin/library/import-items/{itemId}/publish`：仅在解析/转码完成且元数据审核通过后发布到曲库。若导入项关联了 `songRequestId`，发布前会校验曲目标题/艺人是否与需求一致（`SongRequestFulfillment:Checks:TitleArtistMatch`）；不一致时默认拒绝，允许覆盖时需带 `?forceFulfill=true&overrideReason=...`，并写入审计。
- `DELETE /admin/library/import-items/{itemId}/song-request`：解除导入条目与点歌需求的关联（批量上传误挂时逐条解绑），发布时不再自动满足该需求。
- `DELETE /admin/library/import-items/{itemId}`：管理员丢弃导入项并安排隔离文件清理。
- `GET /admin/tracks`：管理端已发布曲目列表；支持按信息缺失、重复项、专辑和艺人筛选。
- `PATCH /admin/tracks/{trackId}`：修正已发布曲目的元数据。
- `PATCH /admin/albums/{albumId}`：修正专辑名、艺人、年份和封面关联。
- `PATCH /admin/artists/{artistId}`：修正艺人名称及简介。
- `PUT /admin/collections/{collectionId}`：维护编辑精选专题的名称、封面、排序和曲目。

管理接口列表均要求管理员；列表使用统一分页结构。曲目 PATCH 的艺人/风格字段按完整集合替换，精选专题 PUT 的 `trackIds` 按数组顺序重建曲目顺序。

文件流式写入隔离目录，校验实际格式、大小和内容哈希。解析失败的文件仍可由管理员手工补全；转码失败时不能发布。隔离文件不能被普通用户或匿名用户访问；服务器磁盘路径永远不返回客户端。上传大小上限从服务器配置读取，具体数值待定。

### 音频格式、解析与转码边界

首版目标覆盖常见音频文件：MP3、FLAC、WAV、AAC、M4A/M4B、Ogg/OGA、WMA、AIFF、APE、Musepack（MPC/MPP）、WavPack（WV）和 DSF。TagLib# 项目列出了这些格式中的相应扩展名，可用于读取常见标签和媒体属性；实际仍需用项目选定的解析器和代表性样本逐格式验证。

解析字段包括标题、艺人、专辑、曲目序号、年份、风格、时长、编码信息和可读取的内嵌封面。标签缺失时保留为空并进入人工补全，不把文件名猜测结果当成权威元数据。扩展名不作为可信依据，后端需要检查文件内容；加密/DRM、损坏、伪装格式或解析器不支持的文件应给出明确失败原因，不能承诺所有历史或专有格式都可解析。

元数据解析成功不代表 Android/HarmonyOS 设备一定能直接播放该编码。首版要求服务端自动生成移动端兼容版本，正式曲目保留原文件和转码版本；API 根据客户端能力返回合适的播放资源。转码器/输出编码与质量仍需用目标设备和实际曲库验证，不能绕过 DRM。

## 首版必需：登录后的收藏与歌单

曲库浏览和播放允许匿名；收藏和歌单跨设备同步需要登录。所有以下接口均要求有效用户身份。

- `GET /me/favorites`：收藏列表。
- `PUT /me/favorites/{trackId}`：收藏曲目；重复调用保持幂等。
- `DELETE /me/favorites/{trackId}`：取消收藏。
- `GET /me/playlists`：我的歌单。
- `POST /me/playlists`：创建歌单。
- `GET /me/playlists/{playlistId}`：歌单详情及曲目。
- `PATCH /me/playlists/{playlistId}`：修改名称、描述或封面。
- `DELETE /me/playlists/{playlistId}`：删除歌单。
- `PUT /me/playlists/{playlistId}/tracks/{trackId}`：加入曲目。
- `DELETE /me/playlists/{playlistId}/tracks/{trackId}`：移出曲目。

账号接口见上方认证章节。歌单与收藏均绑定用户账号；管理员通过角色权限管理曲库和审核，不额外建一套登录接口。

## 后续版本：播放行为与个性化推荐

- `POST /play-events`：批量提交 `play_started`、`progress`、`completed`、`skipped` 等行为。
- `GET /recommendations/daily`：返回个性化每日推荐及推荐原因。
- `GET /recommendations/similar?trackId={trackId}`：按艺人、专辑、风格等元数据返回相似曲目。

用户已确认播放记录与推荐可以滞后；首版首页使用编辑精选和最近加入。推荐逻辑后续仍作为单体内模块，不单独部署微服务。播放进度事件应节流或批量提交，不要每秒请求一次。

## 全局约定

- 列表统一返回 `items`、`page`、`pageSize`、`total`。
- 错误统一返回稳定的错误码和可读消息；不要把磁盘路径、堆栈或内部异常返回给客户端。
- `GET /tracks/{trackId}/stream` 应正确处理 `Range`、`206 Partial Content`、`Content-Range` 和 `Accept-Ranges`。
- 管理端导入、发布、曲库修改和精选维护都要鉴权并写审计记录；公开曲库浏览和已发布曲目播放允许免登录。

## 已确认的首版范围

- 匿名用户可以听歌；登录后才能收藏和使用跨设备歌单。
- 用户登录后可提交歌曲需求并投票；已在曲库的歌曲显示“已在曲库”，不允许用户删除曲目。
- 仅管理员批量上传音频；文件进入隔离区，服务端解析元数据并自动转码，管理员修正并确认后发布。
- 播放记录与个性化推荐后置；首版提供编辑精选。
- 仅管理员邀请注册；邀请通过邮件发送，一次性链接绑定邮箱并限时有效。
- 管理后台与用户端位于同一 Vue 项目，通过用户角色区分页面。
- 邮件服务配置、邀请有效期最终值、音频上传大小限制和转码输出参数仍待确认。