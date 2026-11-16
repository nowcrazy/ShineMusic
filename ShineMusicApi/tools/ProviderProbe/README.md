# 音源真实能力巡检

从仓库根目录运行，需要 .NET 10、ffprobe、生产 provider 所需的 curl/yt-dlp：

```powershell
dotnet run --project .\ShineMusicApi\tools\ProviderProbe\ProviderProbe.csproj -c Release -- "$env:PI_SCRATCH_DIR\provider-probe"
# 第二参数可限定源，逗号分隔
dotnet run --project .\ShineMusicApi\tools\ProviderProbe\ProviderProbe.csproj -c Release -- "$env:PI_SCRATCH_DIR\provider-probe-selected" "qq,kugou"
```

工具读取 API 基础/Development 配置、用户机密与环境变量，仅在当前进程启用被测 provider。Host 不启动，不连业务 DB/MQ，不创建入库任务，不修改生产开关。最多并发两个平台，逐源3个查询，搜索40秒、详情35秒、单次下载100秒预算；直链流限制250MiB。自下载工具内部限制沿用生产实现，取消时由底层子进程处理。输出目录应为会话scratch等临时目录。

平台Cookie内容不打印，报告仅记录凭据存在布尔。下载直链不写入报告，异常URL脱敏。临时音频在探测后清理。每源JSON完成后写入输出目录，全部完成另写report.json。失败重跑请用新目录以保留证据。

字段解释：

- searchStatus/results仅表示接口有数据；matched才表示当前身份规则匹配目标。空结果不能区分无曲目与平台静默风控。
- metadataStatus/returned仅表示详情有数据，detailMatches单独记录目标身份。
- download/audio_received只表示存在ffprobe可解码音频，不等于目标身份、完整性和音质全部达标。
- Attempts保存真实编码、时长、码率、大小等；须与详情/跨源基准比对，不能只看标签。
- 网易已知ID186016、65766可在搜索未匹配时独立验证详情；不把该结果反算为搜索命中。
- Audius/Jamendo使用自身love/piano曲库样本，不能外推中文商业曲库覆盖。
- yt-dlp会提取PCM WAV，PCM码率不能证明原始音轨无损。
- 不能识别所有有损转码FLAC，也未执行人工听辨或音频指纹比对。

本次长期摘要为仓库 `docs/provider-verification.json`。能力状态与默认开关经过人工核对后更新，工具不会自动覆盖配置。平台登录态、代理或接口变化后应重新实测；不要把一次失败视作永久不支持。
