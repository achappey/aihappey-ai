using Microsoft.EntityFrameworkCore;
using AIHappey.Telemetry.Context;
using AIHappey.Telemetry.Models;

namespace AIHappey.Telemetry.Repositories;

public class RequestRepository(AIHappeyTelemetryDatabaseContext databaseContext)
{
    public async Task<List<Request>> GetRequests(CancellationToken cancellationToken = default) =>
        await databaseContext.Requests.AsNoTracking()
            .Include(r => r.User)
            .Include(r => r.Tools)
            .ThenInclude(r => r.Tool)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

    public async Task<Request> CreateRequest(Request request, CancellationToken cancellationToken)
    {
        await databaseContext.Requests.AddAsync(request, cancellationToken);
        await databaseContext.SaveChangesAsync(cancellationToken);

        return request;
    }
}
