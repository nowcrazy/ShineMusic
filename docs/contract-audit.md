# ShineMusic 前后端契约与约束审计

> 审计时间：2026-10-18 19:55
> 审计范围：`backend/ShineMusic.Api`（92 个 .cs，96 个路由）+ `frontend/src`（51 个 ts/vue）+ 现有文档
> 方法：静态检索 + 对运行中的后端（`localhost:5143`）发真实请求验证
> 结论：**架构分层是好的，缺的是"机器可校验"这一层**——所有约束目前靠注释和人肉纪律维持。

---

## 一句话结论

前端的服务层隔离做得很干净（页面里 `http.get/post` 出现 **0 次**，全部走 `services/`），后端的错误响应也有统一类型（`ApiErrorResponse`）。**真正的问题不在"有没有约定"，而在"约定没有被编译器/工具链强制执行"**：

- 后端 96 个路由全部返回 `Task<IResult>`，**0 个**强类型 `Results<>`，**0 个** `.Produces<T>()` → OpenAPI 里没有响应 schema
- 于是前端只能**手写** 54 个 DTO 的 TS 镜像 → 手写必然漂移，且漂移时**不会报错**
- 同一个错误体在 3 种形态间摇摆，前端只认其中 1 种 → 实测有真实的信息丢失

---

## 一、P0：会真实产生错误行为的

### 1.1 错误响应有三套形态，前端只认一套（实测复现）

这是**唯一一个"现在就在坑用户"的问题**。同一个后端，三种错误体：

| 场景 | Content-Type | 实际响应体 | 实测命令 |
| --- | --- | --- | --- |
| 业务错误（404/409/401） | `application/json` | `{"error":{"code":"resource_not_found","message":"...","traceId":"..."}}` | `GET /api/v1/tracks/{不存在的id}` |
| **模型绑定错误**（畸形 JSON、query 类型错） | **`application/problem+json`** | `{"type":"...rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"$":["..."]},"traceId":"..."}` | `POST /api/v1/auth/login -d '{"email": "x", '` |
| **未知路由** | **空** | 0 字节，无 Content-Type | `GET /api/v1/nope` |

实测原始输出：

```
# 畸形 JSON
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json; charset=utf-8
{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"$":["Expected start of a property name or value, but instead reached end of data. Path: $ | LineNumber: 0 | BytePositionInLine: 15."],"request":["The request field is required."]},"traceId":"00-1057084c..."}

# 未知路由
HTTP/1.1 404 Not Found
Content-Type: (空)
(响应体 0 字节)
```

**前端为什么接不住**——`frontend/src/services/http.ts:154-161`：

```ts
const errorBody = (body ?? {}) as ErrorBody   // ErrorBody = { error?: {...} }
const detail = errorBody.error ?? {}           // problem+json 里没有 error 字段 → {}
throw new ApiError(
  status,
  detail.code || (status === 0 ? 'network_error' : 'internal_error'),  // → 'internal_error'
  detail.message || `请求失败：${status}`,                              // → '请求失败：400'
  detail.traceId,
)
```

结果：后端明明告诉了前端「第 0 行第 15 个字符处 JSON 语法错误」，前端却把它压成 **`请求失败：400`**，`traceId` 也丢了。用户和开发者都拿不到真实原因。

**根因**：`Program.cs:73` 只写了 `builder.Services.AddControllers()`，没有配置 `ApiBehaviorOptions.InvalidModelStateResponseFactory`，也没有 `UseStatusCodePages` 处理空 404。而 `[ApiController]` 特性默认接管了模型校验，用的是 ASP.NET 内置的 RFC 7807 格式。

**修法**（约 30 行，一处改动）：

```csharp
// Program.cs
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var first = context.ModelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .Select(kv => $"{kv.Key}: {kv.Value!.Errors[0].ErrorMessage}")
            .FirstOrDefault() ?? "请求参数不合法。";
        return new BadRequestObjectResult(
            new ApiErrorResponse(new ApiErrorDetail("validation_error", first, context.HttpContext.TraceIdentifier)));
    };
});

// 管道里补空 404
app.UseStatusCodePages(async ctx =>
{
    var response = ctx.HttpContext.Response;
    if (response.HasStarted) return;
    await response.WriteAsJsonAsync(
        new ApiErrorResponse(new ApiErrorDetail("resource_not_found", "接口不存在。", ctx.HttpContext.TraceIdentifier)));
});
```

### 1.2 后端 96 个路由，0 个强类型返回，0 个响应声明

```
路由特性总数：96（GET 40 / POST 27 / DELETE 14 / PUT 8 / PATCH 7）
Task<IResult> 返回：94
Task<Results<...>>：0
.Produces<T>() / ProducesResponseType：0
WithName / WithTags：0
```

**影响链**：

1. `IResult` 是黑盒，编译器不知道这个端点返回 `TrackDto` 还是 `PageResult<TrackDto>`
2. OpenAPI 生成器看到 `IResult` 只会写 `200: {}`，**响应 schema 是空的**
3. 于是 `Swashbuckle`（`Program.cs:51`）虽然配了，但产出的 swagger.json 对前端**没有使用价值**
4. 于是前端只能手写 `types/music.ts` 的 34 个接口
5. 手写镜像 → 后端改字段前端不知道 → 运行时 `undefined` → 页面空白，**编译器全程沉默**

这不是理论风险：`SongRequestMatchDto.Kind` 在后端是 `string`（`Contracts/LibraryImportDtos.cs:74`），前端写的是 `'match' | 'fuzzy' | 'mismatch'`（`services/admin-import.ts:58`）。后端哪天加个 `'partial'`，前端 `switch` 走 default，**不报错、不崩溃、只是显示错**。

**修法**：端点签名从

```csharp
public async Task<IResult> GetTracks(...) { return Results.Ok(dto); }
```

改成

```csharp
public async Task<Results<Ok<PageResult<TrackDto>>, BadRequest<ApiErrorResponse>>> GetTracks(...)
```

然后用 `openapi-typescript` 从 `/swagger/v1/swagger.json` 生成 `types/api.generated.ts`，把手写类型替换掉。**这一步做完，"契约漂移"这个问题从"靠人发现"变成"编译期报错"。**

---

## 二、P1：靠人肉纪律维持的

### 2.1 三个错误辅助方法被复制了 26 份

```
ValidationError  复制 11 份：
  ArtworkUploadEndpoints.cs:96    AuthEndpoints.cs:333          CatalogEndpoints.cs:433
  CatalogManagementEndpoints.cs:396  InvitationEndpoints.cs:278  LibraryImportEndpoints.cs:978
  ListeningEndpoints.cs:130       LyricEndpoints.cs:218         SongRequestEndpoints.cs:566
  UserCollectionEndpoints.cs:296  UserUploadEndpoints.cs:339

NotFound         复制 11 份（LyricEndpoints.cs 里有两份：77 和 221）：
  ArtworkEndpoints.cs:65          CatalogEndpoints.cs:436       CatalogManagementEndpoints.cs:399
  InvitationEndpoints.cs:282      LibraryImportEndpoints.cs:980 ListeningEndpoints.cs:133
  LyricEndpoints.cs:77 / :221     SongRequestEndpoints.cs:568   UserCollectionEndpoints.cs:298
  UserUploadEndpoints.cs:341

Error            复制 4 份：
  ArtworkUploadEndpoints.cs:99    LibraryImportEndpoints.cs:984  ProfileEndpoints.cs:172
  UserUploadEndpoints.cs:343
```

这 26 份代码逐字相同。改一处（比如想在 message 里加 i18n，或想统一把 `resource_not_found` 换成别的码）就要改 26 处，**漏一处就是不一致**。`LyricEndpoints.cs` 同一个文件里有两份 `NotFound`，已经说明这件事失控了。

**修法**：抽到 `Endpoints/ApiResults.cs` 一个静态类，26 处改成 `ApiResults.ValidationError(ctx, ...)`。纯机械替换，风险为零。

### 2.2 错误码是裸字符串，两侧各写各的

后端出现次数：

```
"resource_not_found"  18 次
"validation_error"    14 次
"resource_conflict"   13 次
"internal_error"       5 次
"invitation_invalid"   5 次
"authentication_required" 3 次
"song_request_mismatch_override_required" 1 次
```

前端出现次数（`error.code === '...'`，散在 6 个文件）：

```
pages/auth/login.vue:38         authentication_required / validation_error
pages/auth/register.vue:130-132 invitation_invalid / invitation_expired / invitation_redeemed
pages/admin/import.vue:450      song_request_mismatch_override_required
pages/requests/index.vue:141,193,282  resource_conflict
pages/profile/index.vue:147     validation_error
```

**没有任何一处定义了这些码的常量**。后端加一个新码，前端不可能知道；前端写错一个字母，测试也发现不了（`===` 永远 false，走 else 分支，看起来"正常"）。

**修法**：后端加 `public static class ErrorCodes { public const string ValidationError = "validation_error"; ... }`，前端加 `export const ErrorCodes = {...} as const`。更进一步——如果做了 1.2 的 OpenAPI 生成，`code` 字段可以生成成联合类型 `'validation_error' | 'resource_not_found' | ...`，**拼错就编译不过**。

### 2.3 契约类型分两处放，没有规矩

```
types/music.ts          34 个（TrackDto/ApiTrack、ApiMe、ApiCapabilities、PageResult...）
services/*.ts           22 个，其中至少 15 个是后端契约：
  admin-import.ts        ImportSourceFile / ImportMetadata / ImportTranscode / SongRequestMatch
                         / LibraryImportItem / LibraryImportBatch
  admin-collections.ts   CreateCollectionInput / UpdateCollectionInput
  admin-artwork.ts       UploadArtworkResult
  admin-invitation.ts    Invitation
  admin-requests.ts      AdminRequestQuery
  notifications.ts       ApiNotification
  recommend.ts           ApiDailyRecommendation / DailyRecommendation
  requests.ts            SongRequestTimelineItem
  catalog.ts             HomeFeed / TrackQuery
```

`types/music.ts` 开头自己写着「所有 Api* 接口与后端 DTO 一一对应」，但 `ApiNotification`、`ApiDailyRecommendation` 却住在 `services/` 里，命名也不统一（`LibraryImportItem` vs `ApiLibraryImportItem`，`HomeFeed` vs `ApiMusicHomeFeed`）。

**修法**：`types/` 下拆成 `contracts/`（纯后端镜像，只读、不手改）与 `view/`（Track、ThemeMeta 这类界面模型）。契约类型一律 `Api*` 前缀。加一条 ESLint/命名约定。

### 2.4 封闭集合用 `string` 表示

`SongRequestMatchDto.Kind` 是 `string`；`LibraryImportItemDto.Status` 是 `string`；`ImportTranscodeDto.Status` 是 `string`。后端其实有 `LibraryImportItemStatuses`、`SongRequestStatuses` 常量类，但 **DTO 字段没用它们**，所以 OpenAPI 里就是 `type: string`。

**修法**：把这些改成 C# `enum` + `JsonStringEnumConverter`，或者至少 `[JsonConverter]` 让 OpenAPI 生成 `enum: [...]`。前端就能自动拿到联合类型。

---

## 三、P2：工程保障缺失

| 项 | 现状 | 后果 |
| --- | --- | --- |
| 单元测试 | **0 个**（无 xunit / vitest） | 状态机（`SongRequestStatuses` 白名单）、匹配算法（Levenshtein）、令牌轮换全靠手测 |
| CI | **无** `.github/` | 没有"合并前必须 typecheck + build"的闸门 |
| `typecheck` | `package.json` 里有脚本，但**不强制** | 前端类型错误可以长期躺在仓库里 |
| `TreatWarningsAsErrors` | 未开启（`ShineMusic.Api.csproj` 只有 `Nullable` + `ImplicitUsings`） | 可空引用警告只是噪音，不是错误 |
| Analyzers | 无 `Microsoft.CodeAnalysis.NetAnalyzers`、无 `.editorconfig` | 代码风格全靠自觉 |
| 分页默认值 | `20` / `100` 散落 6+ 处（`CatalogEndpoints.cs:17` 有常量，其余各处直接写字面量） | 想改默认页大小要全局搜 |
| 前端错误文案 | `friendlyError()` 在 login.vue、`invitationError()` 在 register.vue、每页各自的 `errorMessage` ref，共 29 个 catch 块 | 同一个错误在不同页面文案不同 |

---

## 四、P3：文档必然漂移

```
代码里的路由：96
api-list.md 记录的端点：68
缺口：28 个（约 29%）
```

抽查未记录的关键路由：

```
precheck   → api-list.md 出现 0 次   （用户上传预检查，前端 library/upload.vue 在用）
feedback   → api-list.md 出现 0 次   （点赞/不感兴趣，前端 feedback.ts 在用）
storage    → api-list.md 出现 0 次   （存储治理，管理端在用）
```

手写文档 + 手写类型 + 无生成器 = 三份契约各自演化。**这也是 1.2 那条（OpenAPI 生成）真正的价值所在：文档不该手写，应该从代码生成。**

---

## 五、整改方案（按投入产出排序）

### A 档：止血（约 1 小时，改动集中、风险低）

1. `Program.cs` 补 `ApiBehaviorOptions.InvalidModelStateResponseFactory` + `UseStatusCodePages` → **错误体统一成一种**
2. 抽 `Endpoints/ApiResults.cs`，删掉 26 份重复辅助方法
3. 后端加 `ErrorCodes` 常量类，替换 56 处裸字符串
4. 前端加 `services/errors.ts`：`ErrorCodes` 常量 + `describeError(err): string` 统一文案
5. 前端契约类型全部收拢到 `types/contracts.ts`，`services/` 只留函数

> 做完这 5 条，"错误体不一致"和"错误码拼错"两类问题就消失了，且**不动任何业务逻辑**。

### B 档：让编译器接管契约（约 4-6 小时）

6. 96 个端点逐步改成 `Results<Ok<T>, BadRequest<ApiErrorResponse>, ...>`，并加 `.Produces<T>()`
7. `npx openapi-typescript http://localhost:5143/swagger/v1/swagger.json -o src/types/api.generated.ts`
8. 删掉手写的 34 个 `Api*` 接口，改成从生成文件 re-export
9. 封闭集合改成 C# `enum`，生成器自动产出联合类型
10. `package.json` 加 `"gen:api"` 脚本，`api-list.md` 改为从 OpenAPI 生成

> 做完之后：**后端改字段 → 前端 `vue-tsc` 直接报错**。这是"约束"从人肉变成机械的分水岭。

### C 档：工程闸门（约 1-2 天）

11. `TreatWarningsAsErrors` + NetAnalyzers + `.editorconfig`
12. 给三个纯函数补单测：状态机白名单、`SongRequestMatchValidator`（Levenshtein）、`TokenService` 轮换
13. GitHub Actions：`dotnet build -warnaserror` + `npm run typecheck` + `dotnet test`
14. 前端分页/错误文案抽公共 composable

---

## 六、建议的落地顺序

**先做 A 档第 1 条**（错误体统一）——它是唯一一个"现在就在坑用户"的，30 行代码，独立可验证。

**再做 B 档第 6-8 条**——这是把"约束"这件事真正解决掉的核心。A 档其余几条（抽常量、收拢类型）如果在 B 档之前做完，B 档接入生成器时还要再改一遍，等于白做。所以：

```
A-1（错误体统一）
  → B-6/7/8（强类型端点 + 生成 TS）
  → A-2/3/4/5（抽常量、收拢类型，此时可基于生成文件做）
  → C（测试 + CI）
```

C 档可以随时并行开始，不依赖前面。

---

## 七、整改执行记录（2026-10-18，A+B+C 全部完成）

以下每条都经过「构建 0 警告 0 错误 / 单测 60 项全过 / 前端 `vue-tsc --noEmit` 0 错误 / 接口实测」验证。

### A 档：止血 —— 已完成

| 项 | 落地物 | 验证 |
| --- | --- | --- |
| 错误响应统一为 `{"error":{code,message,traceId}}` | `Program.cs`：`ApiBehaviorOptions.InvalidModelStateResponseFactory` 接管 `[ApiController]` 默认 RFC 7807；新增兜底中间件把「状态码 ≥400 且 ContentType 为空」的空响应体包装成契约错误体 | curl 实测 6 类场景全部符合预期（见下） |
| 13 份重复辅助方法 → 1 处 | `Endpoints/ApiResults.cs`；14 个 Endpoints 文件删除 62 行定义、约 208 处调用改为 `ApiResults.XXX(HttpContext, ...)` | 构建通过 |
| 错误码常量集中化 | `Contracts/ErrorCodes.cs`（20+ 常量）；约 68 处裸字符串替换 | 构建通过 |
| 错误响应契约声明 | 78 条 `[ProducesResponseType(typeof(ApiErrorResponse), 4xx)]` | OpenAPI 可读 |
| 错误码集中到 `describeError()` | `frontend/src/services/errors.ts`：`ErrorCodes` 常量 + `describeError()` + 字段名中文化 + `shouldReLogin()`；5 个页面接入 | 类型检查通过 |

**错误体实测（本地 5199 端口，重构后逐一 curl）**

| 场景 | 状态码 | 响应体 |
| --- | --- | --- |
| 正常 200 `/system/capabilities` | 200 | 原业务 JSON，未被改写 |
| 未知路由 | 404 | `{"error":{"code":"resource_not_found","message":"接口不存在或请求方式不被支持（HTTP 404）。","traceId":"..."}}`（原为 0 字节空响应） |
| 畸形 JSON | 400 | `{"error":{"code":"validation_error","message":"'b' is an invalid start of a property name...；request: The request field is required.",...}}` |
| query 类型错 `?page=abc` | 400 | `{"error":{"code":"validation_error","message":"page: The value 'abc' is not valid.",...}}` |
| 未登录 `/me` | 401 | `{"error":{"code":"authentication_required","message":"Authentication is required.",...}}` |
| 密码错误 | 401 | `{"error":{"code":"authentication_required","message":"Email or password is invalid.",...}}` |

### B 档：让编译器接管契约 —— 已完成

- **契约来源**：选了「从 C# `public sealed record` 生成 TS」，而非从 OpenAPI 反向生成。理由：本项目 96 个路由全部返回 `Results.Ok(匿名对象)`，OpenAPI 里本来就没有类型信息；而 DTO 是唯一真实存在的类型源，从它生成既不依赖服务端运行、也能进 CI 漂移检测。
- **生成器**：`backend/tools/generate-ts-contracts.py`。支持嵌套泛型、`IReadOnlyList<T>` → `readonly T[]`、`Guid`/`DateTimeOffset` → `string`、nullable、剥离初始化默认值、XML doc → JSDoc；带 `--check` 模式。
- **产物**：`frontend/src/types/api.generated.ts`（75 个契约类型）。
- **单一入口**：`frontend/src/types/music.ts` 重写为「re-export 生成类型 + 命名收口 + 纯前端类型」三节，并在文件头写死规矩：`services/`、`pages/` 里禁止新增契约接口。
- **前端消费**：`services/` 与 `pages/` 全部改从该入口取类型；20+ 处类型错误已清零（readonly 数组、别名撞名、封闭集合收窄三类，见下）。

### C 档：工程闸门 —— 已完成

- `backend/Directory.Build.props`：`TreatWarningsAsErrors=true` + `AnalysisLevel=latest-recommended` + `GenerateDocumentationFile`。
- `backend/.editorconfig`：EF 生成的 `Data/Migrations/**` 整体豁免；13 条 CA 规则显式豁免且**每条都写了理由**（不是为过构建而关规则）。
- 闸门拦住并修掉的真实问题（74 个告警后收敛到 0）：XML 注释非法字符 `&`、`<param>` 名与签名不符、6 处孤立的 XML 文档注释、`Substring` + 字符串拼接改用 `AsSpan`、`Enumerable` 改索引器、`OnModelCreating` 参数命名。
- `backend/ShineMusic.Api.Tests`（xunit，60 项全过）：状态机白名单、`SongRequestMatchValidator`（Levenshtein 三档结论）、`TokenService`（JWT 声明 / refresh 哈希格式 / 确定性 / 滑动过期）。
- `backend/ShineMusic.slnx` + `.github/workflows/ci.yml`：后端 `dotnet build -warnaserror` + `dotnet test`；前端 `npm ci` → `npm run check:contracts`（契约漂移）→ `npm run typecheck` → `npm run build:h5`。
- `frontend/package.json` 新增 `gen:contracts` / `check:contracts`。

### 附带改动

- **`Domain/SongRequestStateMachine.cs`**（新）：把状态流转白名单从 `SongRequestEndpoints` 中抽成纯领域类型，控制器只做 HTTP 边界。行为零变化（端点层原有的 `status != request.Status` 前置判断保留），但现在可被单测锁定。
- **`Contracts/ProfileDtos.cs`**（新）：`/me` 与资料更新改用具名 DTO，替掉匿名对象。

---

## 八、遗留项与已知取舍

### 8.1 封闭集合仍是 `string`（P1-2.4 未根治）

`SongRequestDto.Status`、`LyricDto.Format` 等封闭集合在后端仍以 `string` 承载。**没有改成 `enum` 的原因是 EF 投影**：这三处都在 `.Select(request => new SongRequestDto(..., request.Status, ...))` 的 LINQ-to-Entities 投影里，实体属性是 `string`，改成 enum 需要把投影改成「先取标量再内存映射」或给实体配 `ValueConverter`，后者还会改写已有数据的字节形态（`Enum.ToString()` 产出 `Open` 而非 `open`）。这属于会影响状态机的改动，需要单独一轮 + 回归测试，本次不做。

**当前的收窄方案**：`types/music.ts` 用 `Omit<Generated, 'status'> & { status: SongRequestStatus }` 在「前端唯一契约入口」把 string 收窄成字面量联合，`pages/` 因此有穷尽匹配能力；后端新增取值时重新生成契约会与该文件产生 diff，必须同步追加联合成员（这条已经写进文件注释）。

### 8.2 `readonly` 数组在 UI 层的取舍

生成器对 `IReadOnlyList<T>` 输出 `readonly T[]`（诚实的契约语义）。前端 ref/服务层大多改为 `readonly T[]`；唯一需要原地修改的 `admin/catalog.vue` 在赋值处做了显式拷贝 `tracks.value = [...result.items]`，并注释说明「本地可变副本」。

### 8.3 其他

- `MediaParseFailed` 的注释值保持 `media_parse_failed`（历史契约），没有顺手改成 `metadata_parse_failed`。
- `docs/api-list.md` 与 96 个路由仍有 28 个端点的偏差（`precheck`/`feedback`/`storage` 三组此前完全未记录），建议下一轮从 `IHostedService` 启动钩子或反射扫描 `[Http*]` 特性自动生成。
- 工作区里 `服务器选型对比.html` 已被删除（不在本次改动范围内）；如需保留请执行 `git checkout -- 服务器选型对比.html`。
