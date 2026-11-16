# 歌单解析巡检

从仓库根目录执行（.NET 10）：

```powershell
dotnet run --project .\ShineMusicApi\tools\PlaylistProbe\PlaylistProbe.csproj -- "$env:PI_SCRATCH_DIR\playlist-probe-new" "https://y.qq.com/n/ryqq/playlist/7707261125"
dotnet test .\ShineMusicApi\ShineMusic.Tests\ShineMusic.Tests.csproj --filter FullyQualifiedName~PlaylistParserReliabilityTests
```

生产 Metadata 模块注册、命名 HTTP 客户端和 parser；匿名配置，不启动 Host、DB、MQ 或导入任务。传入链接时只检查指定链接；未传入时检查两个默认网易样本并尝试发现公开QQ歌单。每个解析预算 180 秒，目录请求 25 秒。仅将报告写入指定 scratch 目录。

report.json 包含平台数量、解析数量、缺标题/艺人/来源编号数、重复项、顺序曲目，以及 HTTP 状态/字节数/请求长度和平台原始顺序编号。异常仅记录固定安全消息或异常类型，不打印 Cookie 或网络异常全文。失败不等于平台永久不支持。QQ 若仅返回一页或总数不明会明确失败，不猜测未经验证的分页协议。

本次实测：网易 19723756 为 100/100，3778678 为 200/200；QQ 7707261125 为 66/66。三者缺标题、艺人、来源 ID、重复项均为 0。QQ ID 来自公开 fcg_get_diss_by_tag.fcg 目录（categoryId=10000000, sortId=5, sin=0, ein=1）。首页与旧推荐接口曾失败，保留失败报告后再用发现的 ID 重试成功。结果随平台变化，应重新运行。

## 指定歌单的详情、歌词与并发音频巡检

先运行上述解析工具，再将 report.json 传给 ChainProbe：

```powershell
dotnet run --project .\ShineMusicApi\tools\PlaylistProbe\ChainProbe.csproj -c Release -- "$env:PI_SCRATCH_DIR\playlist-probe-new\report.json" "$env:PI_SCRATCH_DIR\playlist-chain-new"
```

当前适用于网易歌单：对全部曲目按网易ID重新读取详情并通过生产身份匹配器校验，调用生产歌词解析链，优先网易同ID歌词、再走严格跨源匹配；保存来源、排序分、时间戳数、共享时间轴校验结果及各候选拒绝原因。通过状态仍明确未做逐句听校，不输出歌词正文。使用 `--skip-downloads` 只跑详情、歌词与iTunes，避免重复已完成的音频对照。

在歌单首、中、尾等距选择最多12首，串行测试iTunes严格身份匹配（间隔至少3.1秒），随后对同组样本依次执行并发1与3的下载对照。《九张机》不在样本内时另加一次诊断。每首总预算120秒、最多下载3个候选、每文件100MiB上限；候选解析使用生产配置中的有限端点/音质链，不添加端点。记录任务起止、每次响应体传输起止和峰值活动传输数（不包含候选解析、等待响应头、解码）。该指标是响应体读写区间重叠，不是底层TCP连接或瞬时字节流量。

ffprobe核对编码、正码率、采样率、正时长与目标时长差≤5秒；还必须通过 `ffmpeg -xerror` 完整解码，失败不计成功。所有音频在finally删除。FLAC只表示检测到的编码，不能推断原生无损；未进行音频指纹验证。

仅执行下载对照、跳过全部详情/歌词/iTunes请求：

```powershell
dotnet run --project .\ShineMusicApi\tools\PlaylistProbe\ChainProbe.csproj -c Release -- .\docs\verification\playlist-900025217\playlist.json "$env:PI_SCRATCH_DIR\step4-downloads-new" --downloads-only
dotnet run --project .\ShineMusicApi\tools\PlaylistProbe\QueueProbe.csproj -c Release -- "$env:PI_SCRATCH_DIR\step4-queue-new"
```

输出 `downloads-c1.json`、`downloads-c3.json`（以及必要时 `downloads-九张机.json`）。保留 `sampleIndices` 供核对样本一致；诊断仅记录阶段、安全错误类型、HTTP状态和候选数，不保存签名URL、Cookie、响应正文或异常全文。空地址不推断版权/权限原因。全链模式还输出逐曲JSON、all-tracks.json、enrichment.json。

QueueProbe加载appsettings.json中的RabbitMQ连接，使用GUID唯一的 `shinemusic.probe.*` durable队列，结束删除自己的队列；总预算90秒。通过真实RabbitMessageBus.CreateChannelAsync与RabbitQueueConsumerService子类验证并发1/2/3，每组9条中主动失败1条，其余8条完成。普通异常按生产路径nack(requeue:false)，无DLX配置时是丢弃，不声称自动重试。另以关闭未ack通道验证相同消息Redelivered=true。发布直接使用生产创建的隔离通道，避免RabbitMessageBus.PublishAsync附带声明业务队列；不消费、清理或投递正式队列。此测试不涉及数据库认领、业务恢复扫描或歌曲入库。

两工具仅加载appsettings.json，不加载开发配置、User Secrets或环境变量覆盖，不启动业务Host、不写曲库。生产下载2、转码1配置不变。同目录三个探针各自使用独立bin/obj目录，避免依赖解析互相污染。队列报告 `queue.json` 的每项passed字段才是结果（进程成功退出不代表所有检查通过）。
