using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using AIHappey.Telemetry.Context;
using AIHappey.Telemetry.Models;
using Microsoft.EntityFrameworkCore;
using AIHappey.Common.Model;
using System.Threading.Channels;

public record ChatTelemetryRecord(
    ChatRequest ChatRequest,
    string UserId,
    string Username,
    int InputTokens,
    int TotalTokens,
    string ProviderName,
    RequestType RequestType,
    DateTime Started,
    DateTime Ended)
{
    public async Task<Request> ToEntityAsync(AIHappeyTelemetryDatabaseContext db, CancellationToken ct = default)
    {
        // 1) Ensure user
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserId == UserId, ct);
        if (user == null)
        {
            user = new User { UserId = UserId, Username = Username };
            db.Users.Add(user);
        }

        // 2) Ensure provider + model
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Name == ProviderName, ct)
            ?? new Provider { Name = ProviderName };

        var model = await db.Models.FirstOrDefaultAsync(m => m.ModelName == ChatRequest.Model, ct)
            ?? new Model { ModelName = ChatRequest.Model, Provider = provider };

        // 3) Tools
        var toolNames = ChatRequest.Tools?
            .Select(t => t.Name?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var tools = new List<AIHappey.Telemetry.Models.Tool>();
        if (toolNames.Count > 0)
        {
            var existing = await db.Tools.Where(t => toolNames.Contains(t.ToolName)).ToListAsync(ct);
            var existingSet = existing.Select(t => t.ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newOnes = toolNames.Where(n => !existingSet.Contains(n)).Select(n => new AIHappey.Telemetry.Models.Tool { ToolName = n });
            db.Tools.AddRange(newOnes);
            tools.AddRange(existing);
            tools.AddRange(newOnes);
        }

        // 4) Create request + links
        var req = new Request
        {
            RequestId = ChatRequest.Id,
            InputTokens = InputTokens,
            TotalTokens = TotalTokens,
            StartedAt = Started,
            EndedAt = Ended,
            RequestType = RequestType,
            ToolChoice = ChatRequest.ToolChoice,
            Temperature = ChatRequest.Temperature,
            User = user,
            Model = model,
            Tools = tools.Select(t => new RequestTool { Tool = t }).ToList()
        };

        return req;
    }
}



public class ChatTelemetryQueue
{ private readonly Channel<ChatTelemetryRecord> _channel =
        Channel.CreateBounded<ChatTelemetryRecord>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest // never blocks
        });

    public bool TryQueue(ChatTelemetryRecord record) => _channel.Writer.TryWrite(record);

    public IAsyncEnumerable<ChatTelemetryRecord> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

        

    // Fallback (still don't pass the HTTP token!)
    public ValueTask QueueAsync(ChatTelemetryRecord record)
        => _channel.Writer.WriteAsync(record, CancellationToken.None);

    /*
private readonly Channel<ChatTelemetryRecord> _channel =
    Channel.CreateBounded<ChatTelemetryRecord>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest // backpressure
    });

public ValueTask QueueAsync(ChatTelemetryRecord record, CancellationToken ct = default)
    => _channel.Writer.WriteAsync(record, ct);

public IAsyncEnumerable<ChatTelemetryRecord> ReadAllAsync(CancellationToken ct)
    => _channel.Reader.ReadAllAsync(ct);*/
}

public class ChatTelemetryWorker(
    IServiceScopeFactory scopeFactory,
    ChatTelemetryQueue queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // batch buffer
        var buffer = new List<ChatTelemetryRecord>(capacity: 200);
        var flushInterval = TimeSpan.FromMilliseconds(200);

        var lastFlush = DateTime.UtcNow;

        await foreach (var record in queue.ReadAllAsync(stoppingToken))
        {
            buffer.Add(record);

            // flush if buffer full or interval elapsed
            if (buffer.Count >= 200 || DateTime.UtcNow - lastFlush > flushInterval)
            {
                await FlushAsync(buffer, stoppingToken);
                buffer.Clear();
                lastFlush = DateTime.UtcNow;
            }
        }

        // final flush on shutdown
        if (buffer.Count > 0)
            await FlushAsync(buffer, stoppingToken);
    }

    private async Task FlushAsync(List<ChatTelemetryRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AIHappeyTelemetryDatabaseContext>();

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        //        try 
        //       {
        var requests = new List<Request>(records.Count);
        foreach (var r in records)
        {
            var req = await r.ToEntityAsync(db, ct);
            requests.Add(req);
        }

        db.Requests.AddRange(requests);
        await db.SaveChangesAsync(ct);
    }

}
