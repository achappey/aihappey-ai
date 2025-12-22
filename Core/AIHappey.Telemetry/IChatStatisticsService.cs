
namespace AIHappey.Telemetry;

public interface IChatStatisticsService
{
    Task<OverviewStats> GetOverviewAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<TimeBucketStat>> GetDailyActivityAsync(StatsWindow window, CancellationToken ct = default);
    Task<RequestTypeBreakdown> GetRequestTypesAsync(StatsWindow window, CancellationToken ct = default);
    Task<IReadOnlyList<KeyCountStat>> TopUsersAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<IReadOnlyList<ModelUsageStat>> TopModelsAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<IReadOnlyList<KeyCountStat>> TopToolsAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);
    Task<IReadOnlyList<KeyCountStat>> TopProvidersAsync(StatsWindow window, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default);

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
    int SumTotalTokens);

public record TimeBucketStat(DateOnly Date, int Requests, int Users, int Tokens);
public record KeyCountStat(string Key, int Count);
public record ModelUsageStat(string Provider, string Model, int Requests, int Tokens);
public record TokenStats(int MinInput, int P50Input, int P95Input, int MaxInput,
                         int MinTotal, int P50Total, int P95Total, int MaxTotal,
                         double AvgInput, double AvgTotal);
public record LatencyStats(TimeSpan Min, TimeSpan P50, TimeSpan P95, TimeSpan Max, TimeSpan Avg);
public record RequestTypeBreakdown(int Chat, int Sampling, int Completion);

public enum TopOrder { Requests, Tokens, Duration }

public record StatsWindow(DateTime FromUtc, DateTime ToUtc)
{
    public static StatsWindow LastDaysUtc(int days) =>
        new(DateTime.UtcNow.AddDays(-days), DateTime.UtcNow);
}



public sealed record DailyCountStat(DateOnly Day, int Count);

