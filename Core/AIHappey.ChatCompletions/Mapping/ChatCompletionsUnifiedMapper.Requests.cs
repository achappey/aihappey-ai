using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this ChatCompletionOptions request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(request, Json);

        return raw.ToUnifiedRequest(providerId);
    }

    public static AIRequest ToUnifiedRequest(this JsonElement request, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (request.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completions request JSON must be an object.", nameof(request));

        var model = request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;

        var messages = request.TryGetProperty("messages", out var msgEl) && msgEl.ValueKind == JsonValueKind.Array
            ? ParseRequestMessages(msgEl).ToList()
            : [];

        var tools = request.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
            ? ParseTools(toolsEl).ToList()
            : [];

        var metadata = BuildUnifiedRequestMetadata(request);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = model,
            Input = new AIInput
            {
                Items = messages,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.input.raw_messages"] = msgEl.ValueKind == JsonValueKind.Array ? msgEl.Clone() : null
                }
            },
            Temperature = ExtractValue<float?>(request, "temperature"),
            TopP = ExtractValue<double?>(request, "top_p"),
            MaxOutputTokens = ExtractValue<int?>(request, "max_completion_tokens") ?? ExtractValue<int?>(request, "max_tokens"),
            Stream = ExtractValue<bool?>(request, "stream"),
            ParallelToolCalls = ExtractValue<bool?>(request, "parallel_tool_calls"),
            ToolChoice = request.TryGetProperty("tool_choice", out var toolChoiceEl) ? toolChoiceEl.Clone() : null,
            ResponseFormat = request.TryGetProperty("response_format", out var responseFormatEl) ? responseFormatEl.Clone() : null,
            Tools = tools,
            Metadata = metadata
        };
    }

    public static ChatCompletionOptions ToChatCompletionOptions(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedToolChoice = NormalizeToolChoice(request.ToolChoice, request.Tools);
        var additionalProperties = BuildChatCompletionRequestAdditionalProperties(request);

        IEnumerable<object> tools = [.. ToChatTools(request.Tools).ToList() ?? [],
            .. request.Metadata.GetChatCompletionToolDefinitions(providerId) ?? []];

        var options = new ChatCompletionOptions
        {
            Model = request.Model ?? string.Empty,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            Stream = request.Stream,
            Messages = ToChatMessages(request.Input).ToList(),
            Tools = tools,
            ToolChoice = normalizedToolChoice,
            StreamOptions = new StreamOptions()
            {
                IncludeUsage = true
            },
            ResponseFormat = request.ResponseFormat,
            Metadata = request.Metadata,
            Store = false,
            AdditionalProperties = additionalProperties
        };

        providerId.ApplyProviderOptions(request.Metadata, options.AdditionalProperties ??=
                [], ["tools"]);

        options.Metadata = null;

        return options;
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(JsonElement request)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.request.raw"] = request.Clone()
        };

        foreach (var prop in request.EnumerateObject())
            metadata[$"chatcompletions.request.{prop.Name}"] = prop.Value.Clone();

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in request.EnumerateObject())
        {
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["chatcompletions.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }
}
