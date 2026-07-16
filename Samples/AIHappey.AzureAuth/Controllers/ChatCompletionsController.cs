using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.AzureAuth.Extensions;
using AIHappey.Telemetry;

namespace AIHappey.AzureAuth.Controllers;

[ApiController]
[Route("v1/chat/completions")]
public class ChatCompletionsController(IAIModelProviderResolver resolver, IChatTelemetryService chatTelemetryService) : ControllerBase
{
    private readonly IAIModelProviderResolver _resolver = resolver;

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Post([FromBody] ChatCompletionOptions requestDto, CancellationToken cancellationToken)
    {
        if (requestDto == null || requestDto.Messages == null || string.IsNullOrWhiteSpace(requestDto.Model))
            return BadRequest(new { error = "'messages' array and 'model' are required fields" });

        var provider = await _resolver.Resolve(requestDto.Model, cancellationToken);
        if (provider == null)
            return BadRequest(new { error = $"Model '{requestDto.Model}' is not available." });

        requestDto.Model = requestDto.Model.SplitModelId().Model;
        requestDto.Store = false;
        requestDto.StreamOptions ??= new StreamOptions();
        requestDto.StreamOptions.IncludeUsage = true;

        requestDto.Headers = Request.Headers
            .Select(h => new KeyValuePair<string, string?>(h.Key, h.Value.ToString()))
            .GetProviderPassthroughHeaders(provider.GetIdentifier());
        var startedAt = DateTime.UtcNow;

        if (requestDto.Stream == true)
        {
            Response.ContentType = "text/event-stream";
            requestDto.StreamOptions ??= new StreamOptions();
            requestDto.StreamOptions.IncludeUsage = true;

            await using var writer = new StreamWriter(Response.Body);
            ChatCompletionUpdate? usageChunk = null;

            // Stream tokens or chunks, whatever your provider yields!
            await foreach (var chunk in provider.CompleteChatStreamingAsync(requestDto, cancellationToken))
            {
                if (chunk?.Usage != null)
                {
                    usageChunk = chunk;
                }

                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.FlushAsync(cancellationToken);
            }

            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync(cancellationToken);

            await TrackTelemetryAsync(
                usageChunk?.Usage,
                usageChunk?.Model?.SplitModelId().Model ?? requestDto.Model,
                requestDto,
                provider.GetIdentifier(),
                startedAt,
                cancellationToken);

            return new EmptyResult();
        }
        else
        {
            try
            {
                var content = await provider.CompleteChatAsync(requestDto, cancellationToken);

                await TrackTelemetryAsync(
                    content?.Usage,
                    content?.Model?.SplitModelId().Model ?? requestDto.Model,
                    requestDto,
                    provider.GetIdentifier(),
                    startedAt,
                    cancellationToken);

                return Ok(content);
            }
            catch (Exception e)
            {
                return BadRequest(e.Message);
            }
        }
    }

    private async Task TrackTelemetryAsync(
        object? usageObj,
        string? model,
        ChatCompletionOptions requestDto,
        string providerId,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var inputTokens = GetUsageTokenCount(usageObj, "prompt_tokens", "input_tokens") ?? 0;
        var totalTokens = GetUsageTokenCount(usageObj, "total_tokens")
            ?? inputTokens + (GetUsageTokenCount(usageObj, "completion_tokens", "output_tokens") ?? 0);
        var endedAt = DateTime.UtcNow;

        await chatTelemetryService.TrackChatRequestAsync(
            new Vercel.Models.ChatRequest
            {
                Model = model ?? requestDto.Model ?? "unknown",
                Temperature = requestDto.Temperature ?? 1,
                ToolChoice = requestDto.ToolChoice,
                Tools = ExtractTools(requestDto.Tools),
                ResponseFormat = requestDto.ResponseFormat
            },
            HttpContext.GetUserOid()!,
            HttpContext.GetUserUpn()!,
            inputTokens,
            totalTokens,
            providerId,
            Telemetry.Models.RequestType.Completion,
            startedAt,
            endedAt,
            HttpContext.GetAgentId(),
            cancellationToken: cancellationToken);
    }

    private static int? GetUsageTokenCount(object? usageObj, params string[] names)
    {
        if (usageObj == null)
            return null;

        if (usageObj is JsonElement usage)
            return GetUsageTokenCount(usage, names);

        var serializedUsage = JsonSerializer.SerializeToElement(usageObj, JsonSerializerOptions.Web);
        return GetUsageTokenCount(serializedUsage, names);
    }

    private static int? GetUsageTokenCount(JsonElement usage, params string[] names)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in usage.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.TryGetInt32(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static List<Vercel.Models.Tool> ExtractTools(IEnumerable<object>? tools)
    {
        if (tools == null)
            return [];

        var result = new List<Vercel.Models.Tool>();

        foreach (var tool in tools)
        {
            var toolJson = tool is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(tool, JsonSerializerOptions.Web);

            var name = ExtractToolName(toolJson);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new Vercel.Models.Tool
            {
                Name = name,
                Description = ExtractToolDescription(toolJson)
            });
        }

        return [.. result
            .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
    }

    private static string? ExtractToolName(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return null;

        if (tool.TryGetProperty("function", out var function)
            && function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("name", out var functionName)
            && functionName.ValueKind == JsonValueKind.String)
        {
            return functionName.GetString();
        }

        return tool.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
    }

    private static string? ExtractToolDescription(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return null;

        if (tool.TryGetProperty("function", out var function)
            && function.ValueKind == JsonValueKind.Object
            && function.TryGetProperty("description", out var functionDescription)
            && functionDescription.ValueKind == JsonValueKind.String)
        {
            return functionDescription.GetString();
        }

        return tool.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String
            ? description.GetString()
            : null;
    }
}

