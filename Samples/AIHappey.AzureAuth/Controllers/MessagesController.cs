using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Messages;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Telemetry;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/messages")]
public class MessagesController(IAIModelProviderResolver resolver, IChatTelemetryService chatTelemetryService) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;
    private static readonly JsonSerializerOptions Json = MessagesJson.Default;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post(
        [FromBody] MessagesRequest body,
        CancellationToken cancellationToken)
    {
        var model = body.Model;

        if (string.IsNullOrWhiteSpace(model))
            return BadRequest(new { error = "'model' is required" });

        var provider = await _resolver.Resolve(model);
        if (provider == null)
            return BadRequest(new { error = $"Model '{model}' is not available." });

        var startedAt = DateTime.UtcNow;

        // strip provider prefix
        body.Model = model.SplitModelId().Model;

        var headers = Request.Headers
            .Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()))
            .GetProviderPassthroughHeaders(provider.GetIdentifier());
        body.Headers = headers;

        // streaming?
        if (body.Stream == true)
        {
            Response.ContentType = "text/event-stream";

            await using var writer = new StreamWriter(Response.Body);
            MessagesUsage? usage = null;
            string? responseModel = null;

            await foreach (var chunk in provider.MessagesStreamingAsync(body, headers, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(chunk?.Message?.Model))
                {
                    responseModel = chunk.Message.Model;
                }

                usage = MergeUsage(usage, chunk?.Message?.Usage);
                usage = MergeUsage(usage, chunk?.Usage);

                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk, Json)}\n\n");
                await writer.FlushAsync(cancellationToken);
            }

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);

            await TrackTelemetryAsync(
                usage,
                responseModel?.SplitModelId().Model ?? body.Model,
                body,
                provider.GetIdentifier(),
                startedAt,
                cancellationToken);

            return new EmptyResult();
        }

        try
        {
            var result = await provider.MessagesAsync(body, headers, cancellationToken);

            await TrackTelemetryAsync(
                result?.Usage,
                result?.Model?.SplitModelId().Model ?? body.Model,
                body,
                provider.GetIdentifier(),
                startedAt,
                cancellationToken);

            return Ok(result);
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }

    private async Task TrackTelemetryAsync(
        MessagesUsage? usage,
        string? model,
        MessagesRequest request,
        string providerId,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var inputTokens = usage?.InputTokens ?? 0;
        var totalTokens = GetTotalTokens(usage);
        var endedAt = DateTime.UtcNow;

        await chatTelemetryService.TrackChatRequestAsync(
            new Vercel.Models.ChatRequest
            {
                Model = model ?? request.Model ?? "unknown",
                Temperature = request.Temperature ?? 1,
                ToolChoice = request.ToolChoice?.Type,
                MaxOutputTokens = request.MaxTokens,
                Tools = ExtractTools(request.Tools)
            },
            HttpContext.GetUserOid()!,
            HttpContext.GetUserUpn()!,
            inputTokens,
            totalTokens,
            providerId,
            Telemetry.Models.RequestType.Messages,
            startedAt,
            endedAt,
            HttpContext.GetAgentId(),
            cancellationToken: cancellationToken);
    }

    private static int GetTotalTokens(MessagesUsage? usage)
    {
        if (usage == null)
            return 0;

        var cacheCreationInputTokens = usage.CacheCreationInputTokens
            ?? (usage.CacheCreation?.Ephemeral1hInputTokens ?? 0)
            + (usage.CacheCreation?.Ephemeral5mInputTokens ?? 0);

        return (usage.InputTokens ?? 0)
            + (usage.OutputTokens ?? 0)
            + cacheCreationInputTokens
            + (usage.CacheReadInputTokens ?? 0);
    }

    private static MessagesUsage? MergeUsage(MessagesUsage? current, MessagesUsage? update)
    {
        if (update == null)
            return current;

        if (current == null)
            return update;

        current.InputTokens = update.InputTokens ?? current.InputTokens;
        current.OutputTokens = update.OutputTokens ?? current.OutputTokens;
        current.CacheCreationInputTokens = update.CacheCreationInputTokens ?? current.CacheCreationInputTokens;
        current.CacheReadInputTokens = update.CacheReadInputTokens ?? current.CacheReadInputTokens;
        current.CacheCreation = update.CacheCreation ?? current.CacheCreation;

        return current;
    }

    private static List<Vercel.Models.Tool> ExtractTools(IEnumerable<MessageToolDefinition>? tools)
    {
        if (tools == null)
            return [];

        return [.. tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .Select(tool => new Vercel.Models.Tool
            {
                Name = tool.Name!,
                Description = tool.Description
            })
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }
}
