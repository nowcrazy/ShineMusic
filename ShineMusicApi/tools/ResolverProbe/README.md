# ResolverProbe

匿名、小样本的解析/下载诊断工具。只使用 API 的 `appsettings.json`，不读取 `.env`、开发配置、UserSecrets 或环境配置；不会写曲库或数据库。报告只保留固定诊断码、数值权限快照及音频校验结果，不保存下载直链、Cookie 或原始响应正文。

从仓库根目录运行：

```powershell
dotnet build ShineMusicApi/tools/ResolverProbe/ResolverProbe.csproj --configuration Release
dotnet ShineMusicApi/tools/ResolverProbe/bin/Release/net10.0/ResolverProbe.dll ShineMusicApi/.artifacts/resolver-diagnostic tools-directory --resolve-only
```

`tools-directory` 替换为同时含 `ffmpeg`、`ffprobe` 的目录，例如 `ShineMusicApi/tools/ffmpeg/bin`。输出目录由调用者指定；保留旧报告时应使用新目录。

`--resolve-only` 只验证元数据、身份和候选解析，不发起音频下载；报告中的 `acceptancePassed` 为 `null`，取得候选会标为 `candidate_available_not_downloaded`，不能当作下载通过。省略该选项会执行原有的有界下载、时长、编码/码率与完整解码检查，临时音频在结束时删除。

《晴天》ID `186016` 是不可用排查参考样本，不应仅根据元数据 `fee=0` 将其认定为当前可下载的普通免费歌曲。报告分别记录官方访问状态、官方响应中的业务 `code`/是否有 URL、每个回退来源与档位的 HTTP/解析诊断，避免把空候选一律归因到本地下载代码。HTTP 状态 200 不等于响应内存在音频 URL。

退出码：

- `0`：只解析模式完成报告生成；完整模式则要求所有有效样本均下载校验通过、无效 ID 符合预期且短曲发现成功。
- `1`：致命执行错误或临时音频清理失败。
- `2`：参数、媒体工具或启动前置条件错误。
- `3`：完整模式生成了报告，但没有所有样本都通过。已知受限参考曲目仍可能触发此退出码，不能改写为全部通过。
- `130`：用户取消。

报告中的 `resolveOnly`、`cancelled`、`acceptancePassed` 和逐 case 状态优先于控制台摘要。该工具不统计全曲库成功率，不验证真实 LX 脚本，不执行跨进程续传；下载探针自身不是生产队列的 Range 恢复测试，生产恢复逻辑由独立的传输回归用例覆盖。
