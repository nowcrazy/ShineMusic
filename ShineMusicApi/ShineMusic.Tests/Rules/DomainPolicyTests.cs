using ShineMusic.Model.Domain;
using ShineMusic.Model.Entities;
using Xunit;

namespace ShineMusic.Tests.Rules;

/// <summary>领域规则上移后的行为锁定：409 闸门与终态语义都属于设计行为，不允许在重构中漂移。</summary>
public sealed class DomainPolicyTests
{
    private static LibraryImportItem NewItem() => new()
    {
        DisplayName = "test.m4a",
        SourceStorageKey = "sourcedl/test.m4a",
        ContentHash = "hash"
    };

    [Fact]
    public void Publish_gate_requires_all_four_conditions()
    {
        var item = NewItem();
        Assert.False(PublishGate.IsEligible(item));

        item.Status = LibraryImportItemStatuses.ReadyToPublish;
        item.TranscodeStatus = PublishGate.TranscodeSucceeded;
        item.IsMetadataReviewed = true;
        Assert.False(PublishGate.IsEligible(item), "转码产物键为空时不能发布");

        item.TranscodedStorageKey = "transcoded/test.m4a";
        Assert.True(PublishGate.IsEligible(item));

        item.TranscodeStatus = "failed";
        Assert.False(PublishGate.IsEligible(item));
    }

    [Fact]
    public void Publish_gate_lists_all_missing_required_fields()
    {
        var item = NewItem();
        Assert.Equal(new[] { "title", "artists", "duration" }, PublishGate.MissingRequiredFields(item));

        item.Title = "晴天";
        item.ArtistsJson = "[\"周杰伦\"]";
        item.DurationMs = 269000;
        Assert.Empty(PublishGate.MissingRequiredFields(item));
    }

    [Fact]
    public void Malformed_json_is_treated_as_empty_list()
    {
        Assert.Empty(PublishGate.DeserializeList("not-json"));
        Assert.Empty(PublishGate.DeserializeList(null));
        Assert.Equal(2, PublishGate.DeserializeList("[\"a\",\"b\"]").Count);
    }

    [Fact]
    public void Immutable_terminal_and_dismiss_rules_match_endpoint_behavior()
    {
        Assert.True(LibraryImportStateMachine.IsImmutable(LibraryImportItemStatuses.Published));
        Assert.True(LibraryImportStateMachine.IsImmutable(LibraryImportItemStatuses.Rejected));
        Assert.False(LibraryImportStateMachine.IsImmutable(LibraryImportItemStatuses.ProcessingFailed));

        Assert.True(LibraryImportStateMachine.CanDismiss(LibraryImportItemStatuses.ProcessingFailed));
        Assert.False(LibraryImportStateMachine.CanDismiss(LibraryImportItemStatuses.PendingReview));

        // 已拒绝条目允许再次丢弃（与改造前的端点判定一致）。
        Assert.True(LibraryImportStateMachine.CanDiscard(LibraryImportItemStatuses.Rejected));
        Assert.False(LibraryImportStateMachine.CanDiscard(LibraryImportItemStatuses.Published));
        Assert.False(LibraryImportStateMachine.CanDiscard(LibraryImportItemStatuses.Transcoding));
        Assert.True(LibraryImportStateMachine.CanDiscard(LibraryImportItemStatuses.PendingReview));
    }

    [Fact]
    public void Review_only_advances_pending_review_when_metadata_is_complete()
    {
        Assert.Equal(
            LibraryImportItemStatuses.ReadyToPublish,
            LibraryImportStateMachine.StatusAfterReview(LibraryImportItemStatuses.PendingReview, true));
        Assert.Equal(
            LibraryImportItemStatuses.PendingReview,
            LibraryImportStateMachine.StatusAfterReview(LibraryImportItemStatuses.PendingReview, false));
        Assert.Equal(
            LibraryImportItemStatuses.Published,
            LibraryImportStateMachine.StatusAfterReview(LibraryImportItemStatuses.Published, true));
        Assert.False(LibraryImportStateMachine.IsMetadataComplete("  ", 1));
        Assert.True(LibraryImportStateMachine.IsMetadataComplete("晴天", 1));
    }

    [Fact]
    public void Auto_publish_uses_the_threshold_of_the_matching_source()
    {
        var thresholds = new AutoPublishThresholds(SourceEnabled: true, 75, LibraryEnabled: false, 90);
        Assert.True(AutoPublishPolicy.ShouldAutoPublish(true, 75, thresholds));
        Assert.False(AutoPublishPolicy.ShouldAutoPublish(true, 74, thresholds));
        Assert.False(AutoPublishPolicy.ShouldAutoPublish(false, 95, thresholds), "手动导入开关关闭");
        Assert.False(AutoPublishPolicy.ShouldAutoPublish(true, null, thresholds), "无分数永不直通");
    }

    [Fact]
    public void Playback_eligibility_preserves_anonymous_and_private_track_rules()
    {
        Assert.False(PlaybackEligibility.IsAuthenticationRequired(anonymousPlaybackEnabled: true, isAuthenticated: false));
        Assert.True(PlaybackEligibility.IsAuthenticationRequired(anonymousPlaybackEnabled: false, isAuthenticated: false));
        Assert.False(PlaybackEligibility.IsAuthenticationRequired(anonymousPlaybackEnabled: false, isAuthenticated: true));

        var owner = Guid.NewGuid();
        Assert.True(PlaybackEligibility.CanAccess(DateTimeOffset.UtcNow, null, false, null));
        Assert.True(PlaybackEligibility.CanAccess(null, owner, true, null));
        Assert.True(PlaybackEligibility.CanAccess(null, owner, false, owner));
        Assert.False(PlaybackEligibility.CanAccess(null, owner, false, Guid.NewGuid()));
        Assert.False(PlaybackEligibility.CanAccess(null, owner, false, null));
    }
}
