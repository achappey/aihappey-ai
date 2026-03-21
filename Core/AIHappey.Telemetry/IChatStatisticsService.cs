
namespace AIHappey.Telemetry;

public interface IChatStatisticsService
{
    Task<OverviewStats> GetOverviewAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<TimeBucketStat>> GetDailyActivityAsync(StatsWindow window, CancellationToken ct = default);
    Task<RequestTypeBreakdown> GetRequestTypesAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<TopUserStat>> TopUsersAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<UserWindowSummary> GetUserWindowSummaryAsync(StatsWindow window, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default);
    Task<UserAggregatePage> GetUserAggregatesAsync(StatsWindow window, int skip = 0, int take = 100, TopOrder order = TopOrder.Tokens, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default);
    Task<UserAggregateReconciliation> GetUserAggregateReconciliationAsync(StatsWindow window, int top = 200, TopOrder order = TopOrder.Tokens, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default);
    Task<IdentifierHealthReport> GetIdentifierHealthAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<ModelUsageStat>> TopModelsAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<IReadOnlyList<TopToolStat>> TopToolsAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<IReadOnlyList<TopProviderStat>> TopProvidersAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);

    Task<TokenStats> GetTokenStatsAsync(StatsWindow window, CancellationToken ct = default);
    Task<LatencyStats> GetLatencyStatsAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<DailyCountStat>> GetNewUsersPerDayAsync(StatsWindow w, CancellationToken ct = default);

    Task<IReadOnlyList<DailyCountStat>> GetDailyDistinctUsersAsync(StatsWindow w, CancellationToken ct = default);

    /// <summary>Cumulative distinct users per day in the window (growth curve).</summary>
    Task<IReadOnlyList<DailyCountStat>> GetCumulativeDistinctUsersAsync(StatsWindow w, CancellationToken ct = default);
}


public record OverviewStats(
    int Requests,
    int ActiveUsers,
    int DistinctTools,
    int DistinctModels,
    TimeSpan AvgLatency,
    int SumInputTokens,
    int SumOutputTokens,
    int SumTotalTokens);

public record TimeBucketStat(DateOnly Date, int Requests, int Users, int InputTokens, int OutputTokens, int TotalTokens);
public record ModelUsageStat(string Provider, string Model, int Requests, int InputTokens, int OutputTokens, int TotalTokens);
public record TopUserStat(string Key, int Requests, int InputTokens, int OutputTokens, int TotalTokens, int DurationSeconds);
public sealed record UserWindowSummary(
    DateTime FromUtc,
    DateTime ToUtc,
    int TotalDistinctUsers,
    int TotalRequests,
    int TotalInputTokens,
    int TotalOutputTokens,
    int TotalTokens,
    double AvgTokensPerRequest,
    int IncludedDistinctUsers,
    int IncludedRequests,
    int IncludedInputTokens,
    int IncludedOutputTokens,
    int IncludedTotalTokens,
    double IncludedAvgTokensPerRequest,
    int ExcludedDistinctUsers,
    int ExcludedRequests,
    int ExcludedInputTokens,
    int ExcludedOutputTokens,
    int ExcludedTotalTokens,
    IReadOnlyList<string> AppliedNormalizedExclusions);

public sealed record UserAggregatePage(
    DateTime FromUtc,
    DateTime ToUtc,
    int Skip,
    int Take,
    string Order,
    int TotalRowsBeforeExclusions,
    int TotalRows,
    int ReturnedRows,
    bool HasMore,
    int ExcludedUsers,
    int ExcludedTotalTokens,
    IReadOnlyList<string> AppliedNormalizedExclusions,
    IReadOnlyList<UserAggregatePageItem> Items);

public sealed record UserAggregatePageItem(
    int Rank,
    string TelemetryUserId,
    string RawUsername,
    string NormalizedIdentifier,
    bool IsLikelyEmail,
    string? EmailDomain,
    int Requests,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    int DurationSeconds);

public sealed record UserAggregateReconciliation(
    DateTime FromUtc,
    DateTime ToUtc,
    string Order,
    int RequestedTopCount,
    int TotalUsersBeforeExclusions,
    int TotalUsers,
    int ReturnedTopUsers,
    int TotalRequests,
    int TotalTokens,
    int ExcludedUsers,
    int ExcludedTotalTokens,
    int TopUsersRequests,
    int TopUsersTotalTokens,
    double TopUsersRequestCoverage,
    double TopUsersTokenCoverage,
    bool RankingIsComplete,
    bool RankingIsSafeAsExactTotalSource,
    IReadOnlyList<string> AppliedNormalizedExclusions,
    IReadOnlyList<string> Warnings);

public record TopToolStat(string Key, int Requests, int InputTokens, int OutputTokens, int TotalTokens);
public record TopProviderStat(string Key, int Requests, int InputTokens, int OutputTokens, int TotalTokens);
public sealed record IdentifierHealthReport(
    DateTime FromUtc,
    DateTime ToUtc,
    int DistinctUsers,
    int UniqueNormalizedIdentifiers,
    int EmailLikeUsers,
    int NonEmailLikeUsers,
    int NormalizationCollisionCount,
    IReadOnlyList<IdentifierDomainStat> Domains,
    IReadOnlyList<IdentifierCollisionStat> Collisions,
    IReadOnlyList<IdentifierSample> NonEmailSamples);

public sealed record IdentifierDomainStat(string Domain, int Users, int Requests, int TotalTokens);

public sealed record IdentifierCollisionStat(
    string NormalizedIdentifier,
    int UserCount,
    IReadOnlyList<string> RawUsernames,
    IReadOnlyList<string> TelemetryUserIds,
    int Requests,
    int TotalTokens);

public sealed record IdentifierSample(
    string TelemetryUserId,
    string RawUsername,
    string NormalizedIdentifier,
    int Requests,
    int TotalTokens);

public record TokenStats(int MinInputTokens, int P50InputTokens, int P95InputTokens, int MaxInputTokens,
                         int MinOutputTokens, int P50OutputTokens, int P95OutputTokens, int MaxOutputTokens,
                         int MinTotalTokens, int P50TotalTokens, int P95TotalTokens, int MaxTotalTokens,
                         double AvgInputTokens, double AvgOutputTokens, double AvgTotalTokens);
public record LatencyStats(TimeSpan Min, TimeSpan P50, TimeSpan P95, TimeSpan Max, TimeSpan Avg);
public record RequestTypeBreakdown(int Chat, int Sampling, int Completion);

public enum TopOrder { Requests, Tokens, Duration }

public record StatsWindow(DateTime FromUtc, DateTime ToUtc)
{
    public static StatsWindow LastDaysUtc(int days) =>
        new(DateTime.UtcNow.AddDays(-days), DateTime.UtcNow);
}



public sealed record DailyCountStat(DateOnly Day, int Count);

