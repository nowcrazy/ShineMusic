# 音源质量与稳定性综合验收

从仓库根目录执行（需要 .NET 10 SDK）：

```powershell
dotnet test ShineMusicApi/ShineMusic.Tests/ShineMusic.Tests.csproj --filter "Category=SourceDownloadAcceptance"
```

全量回归：

```powershell
dotnet test ShineMusicApi/ShineMusic.Tests/ShineMusic.Tests.csproj
```

全量回归中的 `RealDatabaseStartupTests` 会使用真实 PostgreSQL 和生产启动引导。运行前必须将 `SHINEMUSIC_TEST_POSTGRES_CONNECTION` 指向专用测试库，数据库名必须以 `shinemusic_test` 开头；不会回退到开发库或读取仓库 `.env`。例如在独立测试实例中创建 `shinemusic_test_local`，再在当前 PowerShell 设置该变量后运行。连接串包含测试凭据，应由本地环境或 CI 注入，不提交实际部署凭据。

SQLite 和替身服务的 API 集成测试显式使用 `Testing` 环境，避免加载本地开发基础设施配置。CI 使用独立的 `shinemusic_test_ci` 数据库，并安装 FFmpeg；本地真实媒体测试的工具定位方式见下一节。

真实媒体测试需要同时安装 `ffmpeg` 和 `ffprobe`。测试优先使用 `SHINEMUSIC_TEST_FFMPEG_BIN` 指定目录，其次使用仓库的 `ShineMusicApi/tools/ffmpeg/bin`，最后查找进程 `PATH`；不需要修改系统 `PATH`。Windows 工具来源及本地安装说明见 [tools/ffmpeg/README.md](../../tools/ffmpeg/README.md)。

```powershell
$env:SHINEMUSIC_TEST_FFMPEG_BIN = 'C:\path\to\ffmpeg\bin'
dotnet test ShineMusicApi/ShineMusic.Tests/ShineMusic.Tests.csproj --filter 'Category=FfmpegIntegration|FullyQualifiedName~AssetLifecycleTests.Real_audio_upgrade|FullyQualifiedName~UserUploadChainTests'
```

## 12 项验收标准与测试映射

1. **无损验证**：`A01_A02_Probed_quality_and_metadata_survive_validation_and_import_handoff(true)` 验证完整 FLAC 通过、探测字段完整、文件与入库消息关联正确。
2. **320K 验证**：同一测试的 `false` 分支验证实测 MP3 320kbps 通过，并检查导入记录的真实文件 SHA-256。
3. **编码伪装拦截**：`A03_Mismatched_codec_cannot_create_import` 覆盖 FLAC/MP3 与 WAV/AAC 容器编码不符，拒绝后无导入条目、批次或消息，隔离文件已清理。
4. **试听与占位拦截**：`A04_Preview_including_consistently_mislabelled_source_is_cleaned` 覆盖 11 秒绝对下限、45 秒相对偏差、本源一致错标为 60 秒但跨源为 269 秒。`A04_Short_complete_song_matching_cross_source_is_not_rejected` 防止误伤跨源一致的短曲。
5. **文件完整性**：`A05_File_hash_match_passes_and_tampering_is_rejected` 对真实文件计算 SHA-256，验证匹配与 `hash_mismatch`；`A05_Tampered_download_never_reaches_import_queue` 在真实下载任务探测阶段注入文件修改，验证无法移交导入且清理产物。
6. **同源音质降级**：`A06_Sq_network_or_validation_failure_attempts_hq_in_order` 分别注入 SQ 网络错误和 SQ 试听产物，断言 SQ→HQ 请求顺序、采用 HQ、失败文件清理。
7. **低码率降级门槛**：`A07_Actual_low_bitrate_cannot_bypass_policy_using_high_quality_label` 防止 HQ 标签掩盖实测 128K；`A07_Low_candidate_is_only_downloaded_when_explicitly_allowed` 验证默认不下载 PQ、显式 `AllowLossyFallback=true` 才允许。
8. **多源换源降级**：`A08_Fallback_uses_priority_and_only_unambiguous_identity` 验证失败任务经真实换源服务和聚合器创建新任务，按音源优先级选择，排除原源/禁用源，跳过不同标题、Live 歧义和不同艺人。
9. **故障隔离与预算**：`Faults_and_uncooperative_timeout_are_isolated_from_healthy_sources` 验证单源异常、不配合取消的超时隔离；`Budget_or_external_cancellation_preserves_completed_results_without_penalizing_sources` 验证总预算/外部取消保留完成结果，等待并发槽的源不被误记故障。
10. **熔断触发与拦截**：`Consecutive_business_failures_open_at_default_or_custom_threshold_and_block_requests` 验证默认 5 次及自定义 2/7 次阈值、连续业务故障不被调用成功清零、Open 后不调用 Provider。
11. **冷却与恢复**：`Monitor_and_dispatch_recover_then_escalate_cooldown_with_a_cap` 经真实监控器巡检与调度验证 Open→HalfOpen→Closed、失败立即 Open、冷却 10→20→25→25 封顶、成功后重置；`Hosted_monitor_timer_uses_tracker_clock_and_never_enables_disabled_sources` 验证托管监控定时器也使用同一可控时钟。
12. **配置禁用与故障分类**：上述托管监控测试验证配置禁用不被自动启用；`Non_retriable_business_errors_neither_trip_nor_reset_existing_failures` 覆盖鉴权、无版权、不存在、重复等明确业务错误及大小写处理，不累计熔断，也不清零已有故障。

## 隔离与验证边界

- 下载测试使用独立临时目录、关闭连接池的 SQLite 文件、受控 HTTP 响应及记录消息的总线替身；结束时删除文件和数据库。不会读取开发数据库，不连接真实音源或 RabbitMQ。
- Registry、Orchestrator、HealthTracker、HealthMonitor、SearchAggregator、SourceDownloadJob、SourceDownloadService、SourceDownloadFallbackService、AudioValidator 和 QualityGate 使用生产实现。
- 媒体端口注入可控探测结果；测试验证探测后的业务行为，不验证外部 ffprobe 可用性或音源的实时资源。容器与编码不符检测不能识别所有已转为合法 FLAC 的有损来源。
- SHA-256 证明已下载字节与校验时文件一致；没有来自供应方的可信期望哈希时，不声称证明上游文件真实性。
- 冷却推进不等待真实分钟；故障隔离测试仍使用短秒级真实预算验证非协作超时。
- 未标记 `SourceDownloadAcceptance` 的既有测试不属于该筛选集；全量运行可能包含按环境变量启用的外部集成测试，跳过不能视为通过。

## 本地一键全量回归

本机已有 Docker 和 `postgres:16-alpine` 镜像时，可从仓库根目录运行下面的脚本，支持 Windows PowerShell 5.1 和 PowerShell 7。脚本创建独立的临时 PostgreSQL 容器和随机测试库，仅绑定本机地址；测试结束后还原进程环境并清理该容器，不读取 `.env`。镜像未准备时，先显式执行 `docker pull postgres:16-alpine`。

```powershell
./scripts/Test-Backend.ps1
# 端口占用或使用其他 FFmpeg 安装时：
./scripts/Test-Backend.ps1 -PostgresPort 45440 -FfmpegBin 'C:\path\to\ffmpeg\bin'
# 已完成 Release 构建时：
./scripts/Test-Backend.ps1 -SkipBuild
```

Windows 验收流程中，仅当第⑩道已成功构建当前源码时，才用以下命令替换第⑪道裸 `dotnet test` 命令；构建失败时跳过⑪，避免运行旧产物。独立进程的退出码会涵盖准备环境、测试和清理失败；`45440` 必须是空闲端口。

```powershell
powershell.exe -NoProfile -File .\scripts\Test-Backend.ps1 -SkipBuild -PostgresPort 45440
if ($LASTEXITCODE -ne 0) { $fail += "闸门⑪失败：后端隔离数据库全量测试未通过" }
```

该脚本只为本次运行注入测试库连接，不会永久配置父终端；之后单独执行 `dotnet test` 时仍需显式提供 `SHINEMUSIC_TEST_POSTGRES_CONNECTION`。
