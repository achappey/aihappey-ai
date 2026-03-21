using AIHappey.Telemetry.Context;
using AIHappey.Telemetry.Models;
using Json.Schema;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace AIHappey.Telemetry;

public class ChatStatisticsService(AIHappeyTelemetryDatabaseContext db) : IChatStatisticsService
{
    private static IQueryable<Request> InWindow(AIHappeyTelemetryDatabaseContext db, StatsWindow w) =>
        db.Requests.AsNoTracking()
          .Where(r => r.StartedAt >= w.FromUtc && r.StartedAt < w.ToUtc);

    private sealed record AggregatedUserRow(
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

    private static string NormalizeIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static bool LooksLikeEmail(string normalizedIdentifier)
    {
        if (string.IsNullOrWhiteSpace(normalizedIdentifier))
            return false;

        var at = normalizedIdentifier.IndexOf('@');
        return at > 0 && at == normalizedIdentifier.LastIndexOf('@') && at < normalizedIdentifier.Length - 1;
    }

    private static string? GetEmailDomain(string normalizedIdentifier)
    {
        if (!LooksLikeEmail(normalizedIdentifier))
            return null;

        var at = normalizedIdentifier.IndexOf('@');
        return at >= 0 && at < normalizedIdentifier.Length - 1
            ? normalizedIdentifier[(at + 1)..]
            : null;
    }

    private static string[] NormalizeIdentifiers(IEnumerable<string>? identifiers) =>
        identifiers?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeIdentifier)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray()
        ?? [];

    private static IEnumerable<AggregatedUserRow> OrderUserRows(IEnumerable<AggregatedUserRow> rows, TopOrder order) =>
        order switch
        {
            TopOrder.Tokens => rows.OrderByDescending(r => r.TotalTokens)
                                  .ThenByDescending(r => r.Requests)
                                  .ThenBy(r => r.NormalizedIdentifier, StringComparer.Ordinal),
            TopOrder.Duration => rows.OrderByDescending(r => r.DurationSeconds)
                                    .ThenByDescending(r => r.TotalTokens)
                                    .ThenBy(r => r.NormalizedIdentifier, StringComparer.Ordinal),
            _ => rows.OrderByDescending(r => r.Requests)
                     .ThenByDescending(r => r.TotalTokens)
                     .ThenBy(r => r.NormalizedIdentifier, StringComparer.Ordinal)
        };

    private static (List<AggregatedUserRow> Included, List<AggregatedUserRow> Excluded, string[] AppliedNormalizedExclusions)
        PartitionRows(IEnumerable<AggregatedUserRow> rows, IEnumerable<string>? excludeIdentifiers)
    {
        var normalizedExclusions = NormalizeIdentifiers(excludeIdentifiers);
        if (normalizedExclusions.Length == 0)
            return ([.. rows], [], normalizedExclusions);

        var exclusionSet = normalizedExclusions.ToHashSet(StringComparer.Ordinal);
        var included = new List<AggregatedUserRow>();
        var excluded = new List<AggregatedUserRow>();

        foreach (var row in rows)
        {
            if (exclusionSet.Contains(row.NormalizedIdentifier))
                excluded.Add(row);
            else
                included.Add(row);
        }

        return (included, excluded, normalizedExclusions);
    }

    private static UserWindowSummary BuildUserWindowSummary(
        StatsWindow window,
        IReadOnlyCollection<AggregatedUserRow> allRows,
        IReadOnlyCollection<AggregatedUserRow> includedRows,
        IReadOnlyCollection<AggregatedUserRow> excludedRows,
        IReadOnlyList<string> appliedNormalizedExclusions)
    {
        var totalRequests = allRows.Sum(r => r.Requests);
        var totalInputTokens = allRows.Sum(r => r.InputTokens);
        var totalOutputTokens = allRows.Sum(r => r.OutputTokens);
        var totalTokens = allRows.Sum(r => r.TotalTokens);

        var includedRequests = includedRows.Sum(r => r.Requests);
        var includedInputTokens = includedRows.Sum(r => r.InputTokens);
        var includedOutputTokens = includedRows.Sum(r => r.OutputTokens);
        var includedTotalTokens = includedRows.Sum(r => r.TotalTokens);

        var excludedRequests = excludedRows.Sum(r => r.Requests);
        var excludedInputTokens = excludedRows.Sum(r => r.InputTokens);
        var excludedOutputTokens = excludedRows.Sum(r => r.OutputTokens);
        var excludedTotalTokens = excludedRows.Sum(r => r.TotalTokens);

        return new UserWindowSummary(
            FromUtc: window.FromUtc,
            ToUtc: window.ToUtc,
            TotalDistinctUsers: allRows.Count,
            TotalRequests: totalRequests,
            TotalInputTokens: totalInputTokens,
            TotalOutputTokens: totalOutputTokens,
            TotalTokens: totalTokens,
            AvgTokensPerRequest: totalRequests == 0 ? 0 : (double)totalTokens / totalRequests,
            IncludedDistinctUsers: includedRows.Count,
            IncludedRequests: includedRequests,
            IncludedInputTokens: includedInputTokens,
            IncludedOutputTokens: includedOutputTokens,
            IncludedTotalTokens: includedTotalTokens,
            IncludedAvgTokensPerRequest: includedRequests == 0 ? 0 : (double)includedTotalTokens / includedRequests,
            ExcludedDistinctUsers: excludedRows.Count,
            ExcludedRequests: excludedRequests,
            ExcludedInputTokens: excludedInputTokens,
            ExcludedOutputTokens: excludedOutputTokens,
            ExcludedTotalTokens: excludedTotalTokens,
            AppliedNormalizedExclusions: appliedNormalizedExclusions);
    }

    private async Task<List<AggregatedUserRow>> GetAggregatedUserRowsAsync(StatsWindow w, CancellationToken ct = default)
    {
        var aggregates = await InWindow(db, w)
            .Select(r => new
            {
                r.UserId,
                r.InputTokens,
                OutputTokens = r.TotalTokens - r.InputTokens,
                r.TotalTokens,
                r.StartedAt,
                r.EndedAt
            })
            .GroupBy(x => x.UserId)
            .Select(g => new
            {
                g.Key,
                Requests = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                TotalTokens = g.Sum(x => x.TotalTokens),
                DurationMilliseconds = g.Sum(x =>
                    (long?)EF.Functions.DateDiffMillisecond(x.StartedAt, x.EndedAt) ?? 0L)
            })
            .ToListAsync(ct);

        if (aggregates.Count == 0)
            return [];

        var userIds = aggregates.Select(a => a.Key).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserId, u.Username })
            .ToDictionaryAsync(u => u.Id, ct);

        return [.. aggregates.Select(a =>
        {
            users.TryGetValue(a.Key, out var user);

            var rawUsername = user?.Username ?? $"user:{a.Key}";
            var normalizedIdentifier = NormalizeIdentifier(rawUsername);
            var durationSeconds = a.DurationMilliseconds <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, a.DurationMilliseconds / 1000);

            return new AggregatedUserRow(
                TelemetryUserId: user?.UserId ?? a.Key.ToString(CultureInfo.InvariantCulture),
                RawUsername: rawUsername,
                NormalizedIdentifier: normalizedIdentifier,
                IsLikelyEmail: LooksLikeEmail(normalizedIdentifier),
                EmailDomain: GetEmailDomain(normalizedIdentifier),
                Requests: a.Requests,
                InputTokens: a.InputTokens,
                OutputTokens: a.OutputTokens,
                TotalTokens: a.TotalTokens,
                DurationSeconds: durationSeconds);
        })];
    }

    public async Task<OverviewStats> GetOverviewAsync(StatsWindow w, CancellationToken ct = default)
    {
        var q = InWindow(db, w);

        // latency on the fly (no computed column required)
        var overview = await q.Select(r => new
        {
            r.InputTokens,
            r.TotalTokens,
            OutputTokens = r.TotalTokens - r.InputTokens,
            r.UserId,
            r.ModelId,
            LatMs = EF.Functions.DateDiffMillisecond(r.StartedAt, r.EndedAt)
        }).ToListAsync(ct);

        var requests = overview.Count;
        var activeUsers = overview.Select(x => x.UserId).Distinct().Count();
        var distinctModels = overview.Select(x => x.ModelId).Distinct().Count();
        var tokensIn = overview.Sum(x => x.InputTokens);
        var tokensOut = overview.Sum(x => x.OutputTokens);
        var tokensTot = overview.Sum(x => x.TotalTokens);
        var avgLatency = overview.Count == 0 ? 0.0 : overview.Average(x => x.LatMs);

        // distinct toolnames in window (join via RequestTools)
        var distinctTools = await db.RequestTools.AsNoTracking()
            .Where(rt => q.Select(r => r.Id).Contains(rt.RequestId))
            .Select(rt => rt.ToolId)
            .Distinct()
            .CountAsync(ct);

        return new OverviewStats(
            Requests: requests,
            ActiveUsers: activeUsers,
            DistinctTools: distinctTools,
            DistinctModels: distinctModels,
            AvgLatency: TimeSpan.FromMilliseconds(avgLatency),
            SumInputTokens: tokensIn,
            SumOutputTokens: tokensOut,
            SumTotalTokens: tokensTot);
    }

    public async Task<IReadOnlyList<TimeBucketStat>> GetDailyActivityAsync(
     StatsWindow w, CancellationToken ct = default)
    {
        var baseDay = w.FromUtc.Date;

        var grouped = await InWindow(db, w)
            .Select(r => new
            {
                Offset = EF.Functions.DateDiffDay(baseDay, r.StartedAt), // int
                r.UserId,
                r.InputTokens,
                OutputTokens = r.TotalTokens - r.InputTokens,
                r.TotalTokens
            })
            .GroupBy(x => x.Offset)
            .Select(g => new
            {
                Offset = g.Key,
                Requests = g.Count(),
                Users = g.Select(x => x.UserId).Distinct().Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                TotalTokens = g.Sum(x => x.TotalTokens)
            })
            .OrderBy(x => x.Offset)
            .ToListAsync(ct);

        return [.. grouped
            .Select(x =>
            {
                var day = baseDay.AddDays(x.Offset);
                return new TimeBucketStat(DateOnly.FromDateTime(day), x.Requests, x.Users, x.InputTokens, x.OutputTokens, x.TotalTokens);
            })];
    }


    public async Task<RequestTypeBreakdown> GetRequestTypesAsync(StatsWindow w, CancellationToken ct = default)
    {
        var q = InWindow(db, w).Select(r => r.RequestType);

        var chat = await q.CountAsync(x => x == RequestType.Chat, ct);
        var sampling = await q.CountAsync(x => x == RequestType.Sampling, ct);
        var completion = await q.CountAsync(x => x == RequestType.Completion, ct);

        return new RequestTypeBreakdown(chat, sampling, completion);
    }

    public async Task<IReadOnlyList<TopUserStat>> TopUsersAsync(StatsWindow w, int top = 10,
        TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var rows = await GetAggregatedUserRowsAsync(w, ct);
        return [.. OrderUserRows(rows, order)
            .Take(Math.Max(1, top))
            .Select(r => new TopUserStat(
                r.RawUsername,
                r.Requests,
                r.InputTokens,
                r.OutputTokens,
                r.TotalTokens,
                r.DurationSeconds))];
    }

    public async Task<UserWindowSummary> GetUserWindowSummaryAsync(StatsWindow w, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default)
    {
        var rows = await GetAggregatedUserRowsAsync(w, ct);
        var (included, excluded, appliedNormalizedExclusions) = PartitionRows(rows, excludeIdentifiers);
        return BuildUserWindowSummary(w, rows, included, excluded, appliedNormalizedExclusions);
    }

    public async Task<UserAggregatePage> GetUserAggregatesAsync(StatsWindow w, int skip = 0, int take = 100, TopOrder order = TopOrder.Tokens, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default)
    {
        var normalizedSkip = Math.Max(0, skip);
        var normalizedTake = Math.Clamp(take, 1, 500);

        var rows = await GetAggregatedUserRowsAsync(w, ct);
        var (included, excluded, appliedNormalizedExclusions) = PartitionRows(rows, excludeIdentifiers);
        var ordered = OrderUserRows(included, order).ToList();
        var items = ordered
            .Skip(normalizedSkip)
            .Take(normalizedTake)
            .Select((r, index) => new UserAggregatePageItem(
                Rank: normalizedSkip + index + 1,
                TelemetryUserId: r.TelemetryUserId,
                RawUsername: r.RawUsername,
                NormalizedIdentifier: r.NormalizedIdentifier,
                IsLikelyEmail: r.IsLikelyEmail,
                EmailDomain: r.EmailDomain,
                Requests: r.Requests,
                InputTokens: r.InputTokens,
                OutputTokens: r.OutputTokens,
                TotalTokens: r.TotalTokens,
                DurationSeconds: r.DurationSeconds))
            .ToList();

        return new UserAggregatePage(
            FromUtc: w.FromUtc,
            ToUtc: w.ToUtc,
            Skip: normalizedSkip,
            Take: normalizedTake,
            Order: order.ToString().ToLowerInvariant(),
            TotalRowsBeforeExclusions: rows.Count,
            TotalRows: ordered.Count,
            ReturnedRows: items.Count,
            HasMore: normalizedSkip + items.Count < ordered.Count,
            ExcludedUsers: excluded.Count,
            ExcludedTotalTokens: excluded.Sum(r => r.TotalTokens),
            AppliedNormalizedExclusions: appliedNormalizedExclusions,
            Items: items);
    }

    public async Task<UserAggregateReconciliation> GetUserAggregateReconciliationAsync(StatsWindow w, int top = 200, TopOrder order = TopOrder.Tokens, IEnumerable<string>? excludeIdentifiers = null, CancellationToken ct = default)
    {
        var normalizedTop = Math.Max(1, top);

        var rows = await GetAggregatedUserRowsAsync(w, ct);
        var (included, excluded, appliedNormalizedExclusions) = PartitionRows(rows, excludeIdentifiers);
        var ordered = OrderUserRows(included, order).ToList();
        var topRows = ordered.Take(normalizedTop).ToList();

        var totalRequests = ordered.Sum(r => r.Requests);
        var totalTokens = ordered.Sum(r => r.TotalTokens);
        var topRequests = topRows.Sum(r => r.Requests);
        var topTokens = topRows.Sum(r => r.TotalTokens);
        var rankingIsComplete = ordered.Count == 0 || normalizedTop >= ordered.Count;

        var warnings = new List<string>();
        if (!rankingIsComplete)
            warnings.Add("Top-N ranking does not include all users; do not use it as an exact total source.");

        if (excluded.Count > 0)
            warnings.Add("Exclusions were applied after lower-trim identifier normalization.");

        return new UserAggregateReconciliation(
            FromUtc: w.FromUtc,
            ToUtc: w.ToUtc,
            Order: order.ToString().ToLowerInvariant(),
            RequestedTopCount: normalizedTop,
            TotalUsersBeforeExclusions: rows.Count,
            TotalUsers: ordered.Count,
            ReturnedTopUsers: topRows.Count,
            TotalRequests: totalRequests,
            TotalTokens: totalTokens,
            ExcludedUsers: excluded.Count,
            ExcludedTotalTokens: excluded.Sum(r => r.TotalTokens),
            TopUsersRequests: topRequests,
            TopUsersTotalTokens: topTokens,
            TopUsersRequestCoverage: totalRequests == 0 ? 0 : (double)topRequests / totalRequests,
            TopUsersTokenCoverage: totalTokens == 0 ? 0 : (double)topTokens / totalTokens,
            RankingIsComplete: rankingIsComplete,
            RankingIsSafeAsExactTotalSource: rankingIsComplete,
            AppliedNormalizedExclusions: appliedNormalizedExclusions,
            Warnings: warnings);
    }

    public async Task<IdentifierHealthReport> GetIdentifierHealthAsync(StatsWindow w, CancellationToken ct = default)
    {
        var rows = await GetAggregatedUserRowsAsync(w, ct);

        var domains = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.EmailDomain))
            .GroupBy(r => r.EmailDomain!, StringComparer.Ordinal)
            .Select(g => new IdentifierDomainStat(
                Domain: g.Key,
                Users: g.Count(),
                Requests: g.Sum(x => x.Requests),
                TotalTokens: g.Sum(x => x.TotalTokens)))
            .OrderByDescending(x => x.Users)
            .ThenByDescending(x => x.TotalTokens)
            .ThenBy(x => x.Domain, StringComparer.Ordinal)
            .ToList();

        var collisions = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.NormalizedIdentifier))
            .GroupBy(r => r.NormalizedIdentifier, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => new IdentifierCollisionStat(
                NormalizedIdentifier: g.Key,
                UserCount: g.Count(),
                RawUsernames: [.. g.Select(x => x.RawUsername)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)],
                TelemetryUserIds: [.. g.Select(x => x.TelemetryUserId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)],
                Requests: g.Sum(x => x.Requests),
                TotalTokens: g.Sum(x => x.TotalTokens)))
            .OrderByDescending(x => x.TotalTokens)
            .ThenByDescending(x => x.UserCount)
            .ThenBy(x => x.NormalizedIdentifier, StringComparer.Ordinal)
            .ToList();

        var nonEmailSamples = rows
            .Where(r => !r.IsLikelyEmail)
            .OrderByDescending(r => r.TotalTokens)
            .ThenByDescending(r => r.Requests)
            .ThenBy(r => r.RawUsername, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(r => new IdentifierSample(
                TelemetryUserId: r.TelemetryUserId,
                RawUsername: r.RawUsername,
                NormalizedIdentifier: r.NormalizedIdentifier,
                Requests: r.Requests,
                TotalTokens: r.TotalTokens))
            .ToList();

        return new IdentifierHealthReport(
            FromUtc: w.FromUtc,
            ToUtc: w.ToUtc,
            DistinctUsers: rows.Count,
            UniqueNormalizedIdentifiers: rows.Select(r => r.NormalizedIdentifier)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            EmailLikeUsers: rows.Count(r => r.IsLikelyEmail),
            NonEmailLikeUsers: rows.Count(r => !r.IsLikelyEmail),
            NormalizationCollisionCount: collisions.Count,
            Domains: domains,
            Collisions: collisions,
            NonEmailSamples: nonEmailSamples);
    }

    public async Task<IReadOnlyList<ModelUsageStat>> TopModelsAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var q = InWindow(db, w).Select(r => new
        {
            r.ModelId,
            r.InputTokens,
            OutputTokens = r.TotalTokens - r.InputTokens,
            r.TotalTokens
        });

        var agg = await q.GroupBy(x => x.ModelId)
            .Select(g => new
            {
                g.Key,
                Requests = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                TotalTokens = g.Sum(x => x.TotalTokens)
            })
            .ToListAsync(ct);

        // join to Model + Provider
        var models = await db.Models.AsNoTracking()
            .Where(m => agg.Select(a => a.Key).Contains(m.Id))
            .Select(m => new { m.Id, m.ModelName, m.ProviderId })
            .ToListAsync(ct);

        var providers = await db.Providers.AsNoTracking()
            .Where(p => models.Select(m => m.ProviderId).Distinct().Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var merged = from a in agg
                     join m in models on a.Key equals m.Id
                     let provider = providers.TryGetValue(m.ProviderId, out var pn) ? pn : $"provider:{m.ProviderId}"
                     select new ModelUsageStat(provider, m.ModelName, a.Requests, a.InputTokens, a.OutputTokens, a.TotalTokens);

        var ordered = order == TopOrder.Tokens
            ? merged.OrderByDescending(x => x.TotalTokens)
            : merged.OrderByDescending(x => x.Requests);

        return [.. ordered.Take(top)];
    }

    public sealed record ToolTokenAgg(int ToolId, int InputTokens, int OutputTokens, int TotalTokens);

    public async Task<IReadOnlyList<TopToolStat>> TopToolsAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        // Requests in window -> RequestTools -> Tools
        var requestIds = await InWindow(db, w).Select(r => r.Id).ToListAsync(ct);

        var rt = db.RequestTools.AsNoTracking()
                 .Where(x => requestIds.Contains(x.RequestId));

        var aggReq = await rt.GroupBy(x => x.ToolId)
            .Select(g => new { g.Key, Requests = g.Count() })
            .ToListAsync(ct);

        var tokensPerTool = await (from x in rt
                                   join r in db.Requests.AsNoTracking() on x.RequestId equals r.Id
                                   group r by x.ToolId into g
                                   select new ToolTokenAgg(
                                       g.Key,
                                       g.Sum(r => r.InputTokens),
                                       g.Sum(r => r.TotalTokens - r.InputTokens),
                                       g.Sum(r => r.TotalTokens)))
                        .ToListAsync(ct);

        var toolNames = await db.Tools.AsNoTracking()
            .Where(t => aggReq.Select(a => a.Key).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.ToolName, ct);

        var merged = from a in aggReq
                     join t in tokensPerTool on a.Key equals t.ToolId into tj
                     from t in tj.DefaultIfEmpty(new ToolTokenAgg(a.Key, 0, 0, 0))
                     select new TopToolStat(
                         toolNames.TryGetValue(a.Key, out var n) ? n : $"tool:{a.Key}",
                         a.Requests,
                         t.InputTokens,
                         t.OutputTokens,
                         t.TotalTokens);

        var ordered = order == TopOrder.Tokens
            ? merged.OrderByDescending(x => x.TotalTokens)
            : merged.OrderByDescending(x => x.Requests);

        return [.. ordered.Take(top)];
    }

    public async Task<IReadOnlyList<TopProviderStat>> TopProvidersAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var q = InWindow(db, w)
            .Select(r => new
            {
                r.ModelId,
                r.InputTokens,
                OutputTokens = r.TotalTokens - r.InputTokens,
                r.TotalTokens
            });

        // model -> provider
        var modelToProvider = await db.Models.AsNoTracking()
            .Select(m => new { m.Id, m.ProviderId })
            .ToDictionaryAsync(x => x.Id, x => x.ProviderId, ct);

        var providerAgg = (await q.ToListAsync(ct))
            .GroupBy(x => modelToProvider.TryGetValue(x.ModelId, out var pid) ? pid : -1)
            .Select(g => new
            {
                ProviderId = g.Key,
                Requests = g.Count(),
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                TotalTokens = g.Sum(x => x.TotalTokens)
            })
            .ToList();

        var providerNames = await db.Providers.AsNoTracking()
            .Where(p => providerAgg.Select(a => a.ProviderId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var ordered = order == TopOrder.Tokens
            ? providerAgg.OrderByDescending(a => a.TotalTokens)
            : providerAgg.OrderByDescending(a => a.Requests);

        return [.. ordered.Take(top)
            .Select(a => new TopProviderStat(
                providerNames.TryGetValue(a.ProviderId, out var n) ? n : $"provider:{a.ProviderId}",
                a.Requests,
                a.InputTokens,
                a.OutputTokens,
                a.TotalTokens))];
    }

    public async Task<TokenStats> GetTokenStatsAsync(StatsWindow w, CancellationToken ct = default)
    {
        var q = InWindow(db, w).Select(r => new { r.InputTokens, r.TotalTokens });

        var list = await q.ToListAsync(ct);
        if (list.Count == 0)
            return new TokenStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        static int Pctl(IReadOnlyList<int> data, double p)
        {
            if (data.Count == 0) return 0;
            var idx = (int)Math.Floor((data.Count - 1) * p);
            return data[idx];
        }

        var ins = list.Select(x => x.InputTokens).OrderBy(x => x).ToList();
        var outs = list.Select(x => x.TotalTokens - x.InputTokens).OrderBy(x => x).ToList();
        var tots = list.Select(x => x.TotalTokens).OrderBy(x => x).ToList();

        return new TokenStats(
            MinInputTokens: ins.First(), P50InputTokens: Pctl(ins, 0.50), P95InputTokens: Pctl(ins, 0.95), MaxInputTokens: ins.Last(),
            MinOutputTokens: outs.First(), P50OutputTokens: Pctl(outs, 0.50), P95OutputTokens: Pctl(outs, 0.95), MaxOutputTokens: outs.Last(),
            MinTotalTokens: tots.First(), P50TotalTokens: Pctl(tots, 0.50), P95TotalTokens: Pctl(tots, 0.95), MaxTotalTokens: tots.Last(),
            AvgInputTokens: ins.Average(), AvgOutputTokens: outs.Average(), AvgTotalTokens: tots.Average());
    }

    public async Task<LatencyStats> GetLatencyStatsAsync(StatsWindow w, CancellationToken ct = default)
    {
        var lat = await InWindow(db, w)
            .Select(r => EF.Functions.DateDiffMillisecond(r.StartedAt, r.EndedAt))
            .ToListAsync(ct);

        if (lat.Count == 0)
            return new LatencyStats(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

        lat.Sort();
        static int Pctl(IReadOnlyList<int> data, double p) =>
            data[(int)Math.Floor((data.Count - 1) * p)];

        return new LatencyStats(
            Min: TimeSpan.FromMilliseconds(lat.First()),
            P50: TimeSpan.FromMilliseconds(Pctl(lat, 0.50)),
            P95: TimeSpan.FromMilliseconds(Pctl(lat, 0.95)),
            Max: TimeSpan.FromMilliseconds(lat.Last()),
            Avg: TimeSpan.FromMilliseconds(lat.Average()));
    }

    public async Task<IReadOnlyList<DailyCountStat>> GetNewUsersPerDayAsync(StatsWindow w, CancellationToken ct = default)
    {
        var fromDay = w.FromUtc.Date;
        var toDayExcl = w.ToUtc.Date;

        var firsts = await db.Requests.AsNoTracking()
            .GroupBy(r => r.UserId)
            .Select(g => g.Min(r => r.StartedAt))
            .Where(d => d >= w.FromUtc && d < w.ToUtc)
            .Select(d => d.Date)
            .ToListAsync(ct);

        var counts = firsts.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());

        var days = (toDayExcl - fromDay).Days;
        var result = new List<DailyCountStat>(days);
        for (var i = 0; i < days; i++)
        {
            var day = fromDay.AddDays(i);
            counts.TryGetValue(day, out var c);
            result.Add(new DailyCountStat(DateOnly.FromDateTime(day), c));
        }

        return result;
    }

    // ---------------------------
    // NEW: Daily distinct users
    // ---------------------------
    public async Task<IReadOnlyList<DailyCountStat>> GetDailyDistinctUsersAsync(StatsWindow w, CancellationToken ct = default)
    {
        var fromDay = w.FromUtc.Date;
        var toDayExcl = w.ToUtc.Date;

        // Compute an integer day offset once to keep the query server-side friendly
        var grouped = await InWindow(db, w)
            .Select(r => new
            {
                Offset = EF.Functions.DateDiffDay(fromDay, r.StartedAt), // int per day bucket
                r.UserId
            })
            .GroupBy(x => x.Offset)
            .Select(g => new
            {
                Offset = g.Key,
                Users = g.Select(x => x.UserId).Distinct().Count()
            })
            .OrderBy(x => x.Offset)
            .ToListAsync(ct);

        // Fill the whole window to keep a continuous series (including zeros)
        var days = (toDayExcl - fromDay).Days;
        var byOffset = grouped.ToDictionary(x => x.Offset, x => x.Users);

        var result = new List<DailyCountStat>(days);
        for (var i = 0; i < days; i++)
        {
            var day = fromDay.AddDays(i);
            var users = byOffset.TryGetValue(i, out var c) ? c : 0;
            result.Add(new DailyCountStat(DateOnly.FromDateTime(day), users));
        }

        return result;
    }

    // --------------------------------------------------
    // NEW: Cumulative distinct users per day (growth)
    // --------------------------------------------------
    public async Task<IReadOnlyList<DailyCountStat>> GetCumulativeDistinctUsersAsync(StatsWindow w, CancellationToken ct = default)
    {
        var fromDay = w.FromUtc.Date;
        var toDayExcl = w.ToUtc.Date;

        // First seen date per user (first ever inside window)
        var firstSeenDates = await db.Requests.AsNoTracking()
            .Where(r => r.StartedAt >= w.FromUtc && r.StartedAt < w.ToUtc)
            .GroupBy(r => r.UserId)
            .Select(g => g.Min(r => r.StartedAt))   // earliest activity for that user in the window
            .Select(d => d.Date)                    // truncate to date
            .ToListAsync(ct);

        // Count new users per day, then cumulative sum
        var newPerDay = firstSeenDates
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var days = (toDayExcl - fromDay).Days;
        var result = new List<DailyCountStat>(days);
        var running = 0;

        for (var i = 0; i < days; i++)
        {
            var day = fromDay.AddDays(i);
            if (newPerDay.TryGetValue(day, out var added))
                running += added;

            result.Add(new DailyCountStat(DateOnly.FromDateTime(day), running));
        }

        return result;
    }
}
