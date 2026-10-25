// =============================================================================
// 文件：Controllers/ApiResults.cs
// 用途：全站端点错误响应的唯一构造函数。
//
// 背景：审计发现同一组错误辅助方法在 13 个端点文件里各写了一份——
//   ValidationError 13 份、NotFound 12 份（LyricController 内甚至有两份）、Error 5 份，
//   逐字相同却散落各处。改一处要全局搜，漏一处就出现两套行为。
// 收拢到这里后，错误码、消息模板、状态码只在一处定义。
//
// 调用约定：**一律显式写 `ApiResults.XXX(HttpContext, ...)`**，原因有两个——
//   1. 不要用 `using static` 引入：ControllerBase 自带 NotFound(object)，
//      会把 NotFound(HttpContext) 抢过去解析成 MVC 的 NotFoundObjectResult，返回类型对不上；
//   2. 不要加 `this ControllerBase` 的扩展重载：与 (HttpContext, string) 静态重载同元数时会抢解析
//      （实例写法 ValidationError("field", "msg") 会被错配到双参静态重载）。
//
// 与 Contracts/ErrorCodes 配合使用，禁止在业务代码里再写死错误码字符串。
// =============================================================================
using Microsoft.AspNetCore.Http;
using ShineMusic.Model.Contracts;

namespace ShineMusic.Api.Controllers;

/// <summary>
/// 统一构造 <see cref="ApiErrorResponse"/> 形态的失败结果。
/// 所有 4xx/5xx 的业务响应都应经由此处产出，保证前端只需解析一种 JSON 结构。
/// </summary>
public static class ApiResults
{
    // ===== 400：参数校验失败 =====
    /// <summary>
    /// 字段级校验失败。消息会自动带上「字段名: 」前缀，便于前端定位到具体表单项。
    /// </summary>
    public static IResult ValidationError(HttpContext context, string field, string message) => Results.BadRequest(
        new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, $"{field}: {message}", context.TraceIdentifier)));

    /// <summary>
    /// 无具体字段的校验失败，用于 JSON 反序列化错误或整体语义不合法。
    /// </summary>
    public static IResult ValidationError(HttpContext context, string message) => Results.BadRequest(
        new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ValidationError, message, context.TraceIdentifier)));

    // ===== 404：资源不存在/不可见 =====
    /// <summary>
    /// 目标资源不存在。刻意不区分「真的没有」与「存在但无权访问」，
    /// 统一用 404 避免通过状态码探测他人资源是否存在。
    /// </summary>
    public static IResult NotFound(HttpContext context, string? message = null) => Results.NotFound(
        new ApiErrorResponse(new ApiErrorDetail(
            ErrorCodes.ResourceNotFound,
            message ?? "The requested resource was not found.",
            context.TraceIdentifier)));

    // ===== 409：与资源当前状态冲突 =====
    /// <summary>与资源当前状态冲突（重复提交、状态机不允许的迁移、唯一键冲突等）。</summary>
    public static IResult Conflict(HttpContext context, string message) => Results.Conflict(
        new ApiErrorResponse(new ApiErrorDetail(ErrorCodes.ResourceConflict, message, context.TraceIdentifier)));

    // ===== 任意码：给 403 / 413 / 415 / 429 等非标准语义的场景 =====
    /// <summary>按指定状态码与错误码构造失败响应。</summary>
    public static IResult Error(HttpContext context, int statusCode, string code, string message) => Results.Json(
        new ApiErrorResponse(new ApiErrorDetail(code, message, context.TraceIdentifier)), statusCode: statusCode);
}
