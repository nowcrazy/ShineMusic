// 请求级关联标识的流动容器。
// 由操作日志中间件在每个 HTTP 请求开始时写入 HttpContext.TraceIdentifier，
// 请求结束后还原为先前值；文件日志写入器据此把 TraceId 附加到每一条落盘日志，
// 从而把“一次请求 → 它执行的 SQL → 它抛出的异常 → 它发起的外部调用失败”
// 全部串到同一个 TraceId 上，出问题时不翻遍日志也能顺着 TraceId 快速定位。
// 后台任务（无 HTTP 上下文）不设置，对应日志即不带 TraceId 前缀。

namespace ShineMusic.Common.Logging;

/// <summary>请求级关联标识（TraceId）的流动容器，基于 AsyncLocal 随异步上下文传播。</summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> TraceId = new();

    /// <summary>当前执行流关联的 TraceId；无 HTTP 请求上下文时为 null。</summary>
    public static string? CurrentTraceId
    {
        get => TraceId.Value;
        set => TraceId.Value = value;
    }
}
