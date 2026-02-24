using AIHappey.Telemetry.Context;
using AIHappey.Telemetry.Models;
using Json.Schema;
using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry;

public class ChatStatisticsService(AIHappeyTelemetryDatabaseContext db) : IChatStatisticsService
{
    private static IQueryable<Request> InWindow(AIHappeyTelemetryDatabaseContext db, StatsWindow w) =>
        db.Requests.AsNoTracking()
          .Where(r => r.StartedAt >= w.FromUtc && r.StartedAt < w.ToUtc);

    public async Task<OverviewStats> GetOverviewAsync(StatsWindow w, CancellationToken ct = default)
    {
        var q = InWindow(db, w);

        // latency on the fly (no computed column required)
        var overview = await q.Select(r => new
        {
            r.InputTokens,
            r.TotalTokens,
            r.UserId,
            r.ModelId,
            LatMs = EF.Functions.DateDiffMillisecond(r.StartedAt, r.EndedAt)
        }).ToListAsync(ct);

        var requests = overview.Count;
        var activeUsers = overview.Select(x => x.UserId).Distinct().Count();
        var distinctModels = overview.Select(x => x.ModelId).Distinct().Count();
        var tokensIn = overview.Sum(x => x.InputTokens);
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
                r.TotalTokens
            })
            .GroupBy(x => x.Offset)
            .Select(g => new
            {
                Offset = g.Key,
                Requests = g.Count(),
                Users = g.Select(x => x.UserId).Distinct().Count(),
                Tokens = g.Sum(x => x.TotalTokens)
            })
            .OrderBy(x => x.Offset)
            .ToListAsync(ct);

        return [.. grouped
            .Select(x =>
            {
                var day = baseDay.AddDays(x.Offset);
                return new TimeBucketStat(DateOnly.FromDateTime(day), x.Requests, x.Users, x.Tokens);
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

    public async Task<IReadOnlyList<KeyCountStat>> TopUsersAsync(StatsWindow w, int top = 10,
        TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var q = InWindow(db, w)
            .Select(r => new { r.UserId, r.TotalTokens, r.StartedAt, r.EndedAt });

        var agg = await q
            .GroupBy(x => x.UserId)
            // .ToList()
            .Select(g => new
            {
                g.Key,
                Requests = g.Count(),
                Tokens = g.Sum(x => x.TotalTokens),
                Duration = g.Sum(x =>
                    (long?)EF.Functions.DateDiffMillisecond(x.StartedAt, x.EndedAt) ?? 0L)
            })
            .ToListAsync();

        var users = await db.Users.AsNoTracking()
            .Where(u => agg.Select(a => a.Key).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var ordered = order == TopOrder.Tokens
            ? agg.OrderByDescending(a => a.Tokens)
            : order == TopOrder.Requests ?
            agg.OrderByDescending(a => a.Requests) : agg.OrderByDescending(a => a.Duration);

        return [.. ordered.Take(top)
            .Select(a => new KeyCountStat(
                users.TryGetValue(a.Key, out var name) ? name : $"user:{a.Key}",
                 order switch
                 {
                     TopOrder.Tokens => Math.Min(a.Tokens, int.MaxValue),
                     TopOrder.Requests => a.Requests,
                     _ => (int)a.Duration / 1000 // convert ms → seconds here
                 }
            ))];

    }

    public async Task<IReadOnlyList<ModelUsageStat>> TopModelsAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var q = InWindow(db, w).Select(r => new { r.ModelId, r.TotalTokens });

        var agg = await q.GroupBy(x => x.ModelId)
            .Select(g => new { g.Key, Requests = g.Count(), Tokens = g.Sum(x => x.TotalTokens) })
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
                     select new ModelUsageStat(provider, m.ModelName, a.Requests, a.Tokens);

        var ordered = order == TopOrder.Tokens
            ? merged.OrderByDescending(x => x.Tokens)
            : merged.OrderByDescending(x => x.Requests);

        return [.. ordered.Take(top)];
    }

    public sealed record ToolTokenAgg(int ToolId, int Tokens);

    public async Task<IReadOnlyList<KeyCountStat>> TopToolsAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        // Requests in window -> RequestTools -> Tools
        var requestIds = await InWindow(db, w).Select(r => r.Id).ToListAsync(ct);

        var rt = db.RequestTools.AsNoTracking()
                 .Where(x => requestIds.Contains(x.RequestId));

        var aggReq = await rt.GroupBy(x => x.ToolId)
            .Select(g => new { g.Key, Requests = g.Count() })
            .ToListAsync(ct);

        var tokensPerTool = order == TopOrder.Tokens
                ? await (from x in rt
                         join r in db.Requests.AsNoTracking() on x.RequestId equals r.Id
                         group r by x.ToolId into g
                         select new ToolTokenAgg(g.Key, g.Sum(r => r.TotalTokens)))
                        .ToListAsync(ct)
                : new List<ToolTokenAgg>();

        var toolNames = await db.Tools.AsNoTracking()
            .Where(t => aggReq.Select(a => a.Key).Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.ToolName, ct);

        IEnumerable<(int id, int val)> ordered =
            order == TopOrder.Tokens
            ? aggReq.Join(tokensPerTool, a => a.Key, t => t.ToolId, (a, t) => (id: a.Key, val: t.Tokens))
                    .OrderByDescending(x => x.val)
            : aggReq.Select(a => (id: a.Key, val: a.Requests))
                    .OrderByDescending(x => x.val);

        return [.. ordered.Take(top).Select(x => new KeyCountStat(toolNames.TryGetValue(x.id, out var n) ? n : $"tool:{x.id}", x.val))];
    }

    public async Task<IReadOnlyList<KeyCountStat>> TopProvidersAsync(StatsWindow w, int top = 10, TopOrder order = TopOrder.Requests, CancellationToken ct = default)
    {
        var q = InWindow(db, w)
            .Select(r => new { r.ModelId, r.TotalTokens });

        // model -> provider
        var modelToProvider = await db.Models.AsNoTracking()
            .Select(m => new { m.Id, m.ProviderId })
            .ToDictionaryAsync(x => x.Id, x => x.ProviderId, ct);

        var providerAgg = (await q.ToListAsync(ct))
            .GroupBy(x => modelToProvider.TryGetValue(x.ModelId, out var pid) ? pid : -1)
            .Select(g => new { ProviderId = g.Key, Requests = g.Count(), Tokens = g.Sum(x => x.TotalTokens) })
            .ToList();

        var providerNames = await db.Providers.AsNoTracking()
            .Where(p => providerAgg.Select(a => a.ProviderId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var ordered = order == TopOrder.Tokens
            ? providerAgg.OrderByDescending(a => a.Tokens)
            : providerAgg.OrderByDescending(a => a.Requests);

        return [.. ordered.Take(top)
            .Select(a => new KeyCountStat(providerNames.TryGetValue(a.ProviderId, out var n) ? n : $"provider:{a.ProviderId}",
                                          order == TopOrder.Tokens ? a.Tokens : a.Requests))];
    }

    public async Task<TokenStats> GetTokenStatsAsync(StatsWindow w, CancellationToken ct = default)
    {
        var q = InWindow(db, w).Select(r => new { r.InputTokens, r.TotalTokens });

        var list = await q.ToListAsync(ct);
        if (list.Count == 0)
            return new TokenStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        static int Pctl(IReadOnlyList<int> data, double p)
        {
            if (data.Count == 0) return 0;
            var idx = (int)Math.Floor((data.Count - 1) * p);
            return data[idx];
        }

        var ins = list.Select(x => x.InputTokens).OrderBy(x => x).ToList();
        var tots = list.Select(x => x.TotalTokens).OrderBy(x => x).ToList();

        return new TokenStats(
            MinInput: ins.First(), P50Input: Pctl(ins, 0.50), P95Input: Pctl(ins, 0.95), MaxInput: ins.Last(),
            MinTotal: tots.First(), P50Total: Pctl(tots, 0.50), P95Total: Pctl(tots, 0.95), MaxTotal: tots.Last(),
            AvgInput: ins.Average(), AvgTotal: tots.Average());
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
