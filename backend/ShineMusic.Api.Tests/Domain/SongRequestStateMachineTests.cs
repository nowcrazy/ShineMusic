using ShineMusic.Api.Data;
using ShineMusic.Api.Domain;
using ShineMusic.Api.Options;

namespace ShineMusic.Api.Tests.Domain;

/// <summary>
/// 点歌需求状态机白名单测试。
///
/// 锁定三类规则，任何回归都会在这里被拦住：
///   1) 只允许「向前」流转，禁止回退（fulfilled→open 之类）；
///   2) 终态 fulfilled / withdrawn 没有正常出口；
///   3) declined→open 是唯一例外，且必须走专用 reopen 端点（本方法同样放行，端点层做权限区分）；
///   4) 未知状态一律 fail-closed。
/// </summary>
public sealed class SongRequestStateMachineTests
{
    [Theory]
    // open 可去任何受理中/终态，也允许直接 fulfilled（已确认入库）
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Declined)]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Withdrawn)]
    // planned 可继续推进、拒绝、撤回；但不能回到 open
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.Declined)]
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.Withdrawn)]
    // in_progress 可入库或拒绝；不能撤回（已进入处理）
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Declined)]
    // declined → open：唯一合法回退，管理员补到音源后重新开启
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.Open)]
    public void IsTransitionAllowed_允许的流转_返回True(string from, string to) =>
        Assert.True(SongRequestStateMachine.IsTransitionAllowed(from, to));

    [Theory]
    // 回退一律禁止：已推进的进度不能被悄悄重置
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.Fulfilled, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.Fulfilled, SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.Fulfilled, SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.Fulfilled, SongRequestStatuses.Declined)]
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.Withdrawn)]
    [InlineData(SongRequestStatuses.Withdrawn, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.Withdrawn, SongRequestStatuses.Fulfilled)]
    // in_progress 之后不允许撤回：进入处理后不能单方面撤销
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Withdrawn)]
    public void IsTransitionAllowed_禁止的流转_返回False(string from, string to) =>
        Assert.False(SongRequestStateMachine.IsTransitionAllowed(from, to));

    [Theory]
    [InlineData(SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Withdrawn)]
    public void IsTerminal_终态_返回True(string status) =>
        Assert.True(SongRequestStateMachine.IsTerminal(status));

    [Theory]
    [InlineData(SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.Declined)]
    public void IsTerminal_非终态_返回False(string status) =>
        Assert.False(SongRequestStateMachine.IsTerminal(status));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OPEN")]
    [InlineData("unknown_status")]
    [InlineData("Fulfilled ")]
    public void IsTransitionAllowed_未知或未归一化状态_一律拒绝(string from) =>
        Assert.False(SongRequestStateMachine.IsTransitionAllowed(from, SongRequestStatuses.Planned));

    [Fact]
    public void IsKnownStatus_覆盖白名单全部取值()
    {
        foreach (var status in SongRequestStatuses.All)
            Assert.True(SongRequestStateMachine.IsKnownStatus(status), $"{status} 应被识别为已知状态");

        Assert.False(SongRequestStateMachine.IsKnownStatus("archived"), "白名单外取值不应被识别");
    }

    [Fact]
    public void 白名单_不含同态流转_由端点层的状态变化判断兜底()
    {
        // 端点层只在「状态确实发生变化」时才调用 IsTransitionAllowed（见 SongRequestEndpoints 的
        // `status != request.Status && !IsTransitionAllowed(...)`），因此状态机本身不允许
        // x → x：把「重复提交同一状态」显式判为非法变更，避免调用方漏掉变化判断时静默放行。
        foreach (var status in SongRequestStatuses.All)
            Assert.False(SongRequestStateMachine.IsTransitionAllowed(status, status), $"{status} → {status} 应判为非法变更");
    }
}
