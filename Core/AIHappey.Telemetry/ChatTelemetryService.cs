using AIHappey.Telemetry.Context;
using AIHappey.Telemetry.Models;
using AIHappey.Vercel.Models;
using Microsoft.EntityFrameworkCore;

namespace AIHappey.Telemetry;

public class ChatTelemetryService(AIHappeyTelemetryDatabaseContext _db) : IChatTelemetryService
{
    //private readonly AIHappeyTelemetryDatabaseContext _db = db;

    public async Task TrackChatRequestAsync(
     ChatRequest chatRequest,
     string userId,
     string username,
     int inputTokens,
     int totalTokens,
     string providerName,
     RequestType requestType,
     DateTime started, DateTime ended,
     CancellationToken cancellationToken = default)
    {
        // 1. Ensure the User exists
        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.UserId == userId, cancellationToken);

        if (user == null)
        {
            user = new User { UserId = userId, Username = username };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.Equals(user.Username, username, StringComparison.Ordinal))
        {
            // keep username in sync if changed
            user.Username = username;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var prefix = $"{providerName}/";

        var modelId = chatRequest.Model.StartsWith(prefix)
            ? chatRequest.Model[prefix.Length..]
            : chatRequest.Model;

        var model = await _db.Models
                   .FirstOrDefaultAsync(u => u.ModelName == modelId, cancellationToken);

        if (model == null)
        {
            var providerItem = await _db.Providers
                              .FirstOrDefaultAsync(u => u.Name == providerName, cancellationToken);

            if (providerItem == null)
            {
                providerItem = new Provider { Name = providerName };
                await _db.Providers.AddAsync(providerItem);
                await _db.SaveChangesAsync(cancellationToken);
            }

            model = new Model { ModelName = modelId, ProviderId = providerItem.Id };
            _db.Models.Add(model);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // 2. Collect tool names
        var toolNames = chatRequest.Tools?
            .Select(t => t.Name?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        // 3. Load all existing tools
        var existingTools = await _db.Tools
            .Where(t => toolNames.Contains(t.ToolName))
            .ToListAsync(cancellationToken);

        // 4. Add missing tools
        var existingNames = existingTools
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newTools = toolNames
            .Where(n => !existingNames.Contains(n))
            .Select(n => new Models.Tool { ToolName = n })
            .ToList();

        if (newTools.Count > 0)
        {
            _db.Tools.AddRange(newTools);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // combine all tools now present in DB
        var allTools = await _db.Tools
            .Where(t => toolNames.Contains(t.ToolName))
            .ToListAsync(cancellationToken);

        // 5. Build the Request entity
        var request = new Request
        {
            RequestId = chatRequest.Id,
            InputTokens = inputTokens,
            TotalTokens = totalTokens,
            ModelId = model.Id,
            StartedAt = started,
            EndedAt = ended,
            RequestType = requestType,
            ToolChoice = chatRequest.ToolChoice,
            Temperature = chatRequest.Temperature,
            UserId = user.Id,
            Tools = [.. allTools.Select(t => new RequestTool { ToolId = t.Id })]
        };

        _db.Requests.Add(request);
        await _db.SaveChangesAsync(cancellationToken);
    }
}

