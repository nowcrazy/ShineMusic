using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>锁定点歌需求状态机：重构只允许搬位置，不允许改变流转语义。</summary>
public sealed class SongRequestStateMachineTests
{
    [Theory]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Planned)]
    [InlineData(SongRequestStatuses.Open, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.InProgress)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Fulfilled)]
    [InlineData(SongRequestStatuses.Declined, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.Withdrawn, SongRequestStatuses.Open)]
    public void Allows_documented_transitions(string from, string to)
    {
        Assert.True(SongRequestStateMachine.IsTransitionAllowed(from, to));
    }

    [Theory]
    [InlineData(SongRequestStatuses.Fulfilled, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.InProgress, SongRequestStatuses.Withdrawn)]
    [InlineData(SongRequestStatuses.Planned, SongRequestStatuses.Open)]
    [InlineData("unknown", SongRequestStatuses.Open)]
    [InlineData(SongRequestStatuses.Open, "unknown")]
    public void Rejects_backwards_or_unknown_transitions(string from, string to)
    {
        Assert.False(SongRequestStateMachine.IsTransitionAllowed(from, to));
    }

    [Fact]
    public void Fulfilled_is_the_only_irreversible_terminal_state()
    {
        Assert.True(SongRequestStateMachine.IsTerminal(SongRequestStatuses.Fulfilled));
        Assert.False(SongRequestStateMachine.IsTerminal(SongRequestStatuses.Declined));
        Assert.False(SongRequestStateMachine.IsTerminal(SongRequestStatuses.Withdrawn));
        Assert.False(SongRequestStateMachine.IsTerminal(SongRequestStatuses.Open));
    }

    [Fact]
    public void Known_status_check_is_fail_closed()
    {
        Assert.True(SongRequestStateMachine.IsKnownStatus(SongRequestStatuses.Open));
        Assert.False(SongRequestStateMachine.IsKnownStatus("OPEN"));
        Assert.False(SongRequestStateMachine.IsKnownStatus(string.Empty));
    }
}
