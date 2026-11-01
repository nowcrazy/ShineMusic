using ShineMusic.Model.Contracts;
using ShineMusic.Model.Entities;
using SqlSugar;

namespace ShineMusic.Service;

/// <summary>读取及排期个性化推荐策略；未配置时保留当前算法的默认权重。</summary>
public sealed class RecommendationStrategyService(ISqlSugarClient db)
{
    private static readonly RecommendationStrategyValuesDto Defaults = new(
        DailyTrackCount: 30,
        ArtistAffinityWeight: 2.5,
        PlayCountWeight: 3,
        PlayCountCap: 20,
        SkipPenalty: 4,
        FavoriteBoost: 6,
        LikeBoost: 8,
        DislikePenalty: 40,
        NonRepeatDays: 7,
        RecentRecommendationPenalty: 25,
        NewTrackBoostDays: 90,
        NewTrackBoostMax: 3);

    public async Task<RecommendationStrategyValuesDto> GetActiveAsync(
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var dateKey = ToDateKey(date);
        var version = await db.Queryable<RecommendationStrategyVersion>()
            .Where(item => item.EffectiveDateKey <= dateKey)
            .OrderByDescending(item => item.EffectiveDateKey)
            .FirstAsync(cancellationToken);
        return version is null ? Defaults : ToValues(version);
    }

    public async Task<RecommendationStrategyAdminDto> GetAdminAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var todayKey = ToDateKey(today);
        var active = await db.Queryable<RecommendationStrategyVersion>()
            .Where(item => item.EffectiveDateKey <= todayKey)
            .OrderByDescending(item => item.EffectiveDateKey)
            .FirstAsync(cancellationToken);
        var scheduled = await db.Queryable<RecommendationStrategyVersion>()
            .Where(item => item.EffectiveDateKey > todayKey)
            .OrderBy(item => item.EffectiveDateKey)
            .FirstAsync(cancellationToken);

        return new RecommendationStrategyAdminDto(
            active is null ? Defaults : ToValues(active),
            scheduled is null ? null : ToValues(scheduled),
            scheduled is null ? null : FromDateKey(scheduled.EffectiveDateKey));
    }

    public async Task<DateOnly> ScheduleForNextDayAsync(
        UpdateRecommendationStrategyRequest request,
        Guid adminUserId,
        CancellationToken cancellationToken)
    {
        var effectiveDate = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        var effectiveDateKey = ToDateKey(effectiveDate);
        var existing = await db.Queryable<RecommendationStrategyVersion>()
            .FirstAsync(item => item.EffectiveDateKey == effectiveDateKey, cancellationToken);
        var version = existing ?? new RecommendationStrategyVersion
        {
            EffectiveDateKey = effectiveDateKey
        };

        version.DailyTrackCount = request.DailyTrackCount;
        version.ArtistAffinityWeight = request.ArtistAffinityWeight;
        version.PlayCountWeight = request.PlayCountWeight;
        version.PlayCountCap = request.PlayCountCap;
        version.SkipPenalty = request.SkipPenalty;
        version.FavoriteBoost = request.FavoriteBoost;
        version.LikeBoost = request.LikeBoost;
        version.DislikePenalty = request.DislikePenalty;
        version.NonRepeatDays = request.NonRepeatDays;
        version.RecentRecommendationPenalty = request.RecentRecommendationPenalty;
        version.NewTrackBoostDays = request.NewTrackBoostDays;
        version.NewTrackBoostMax = request.NewTrackBoostMax;
        version.UpdatedAtUtc = DateTime.UtcNow;
        version.UpdatedByUserId = adminUserId;

        if (existing is null)
            await db.Insertable(version).ExecuteCommandAsync(cancellationToken);
        else
            await db.Updateable(version).ExecuteCommandAsync(cancellationToken);

        return effectiveDate;
    }

    private static RecommendationStrategyValuesDto ToValues(RecommendationStrategyVersion version) => new(
        version.DailyTrackCount,
        version.ArtistAffinityWeight,
        version.PlayCountWeight,
        version.PlayCountCap,
        version.SkipPenalty,
        version.FavoriteBoost,
        version.LikeBoost,
        version.DislikePenalty,
        version.NonRepeatDays,
        version.RecentRecommendationPenalty,
        version.NewTrackBoostDays,
        version.NewTrackBoostMax);

    private static int ToDateKey(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;

    private static DateOnly FromDateKey(int dateKey) =>
        new(dateKey / 10000, dateKey / 100 % 100, dateKey % 100);
}
