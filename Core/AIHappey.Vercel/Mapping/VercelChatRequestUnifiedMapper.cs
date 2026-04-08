using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping.Abstractions;
using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Mapping;

public static  class VercelChatRequestUnifiedMapper 
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;   
  
    /*public AIRequest ToUnifiedRequest(ChatRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var inputItems = request.Messages?.Select(_uiMapper.ToUnifiedInputItem).ToList() ?? [];

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Input = new AIInput
            {
                Items = inputItems,
                Metadata = new Dictionary<string, object?>
                {
                    ["vercel.chat.id"] = request.Id,
                    ["vercel.providerMetadata"] = request.ProviderMetadata,
                    ["vercel.maxToolCalls"] = request.MaxToolCalls,
                    ["vercel.responseFormat"] = request.ResponseFormat
                }
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = new Dictionary<string, object?>
            {
                ["vercel.chat.id"] = request.Id,
                ["vercel.providerMetadata"] = request.ProviderMetadata,
                ["vercel.maxToolCalls"] = request.MaxToolCalls,
                ["vercel.responseFormat"] = request.ResponseFormat
            }
        };
    }

    public ChatRequest ToChatRequest(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();
        var input = request.Input;
        var inputMetadata = input?.Metadata ?? new Dictionary<string, object?>();

        var outputMessages = (input?.Items ?? [])
            .Select(ToUIMessageFromInput)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        return new ChatRequest
        {
            Id = ExtractValue<string>(metadata, "vercel.chat.id")
                 ?? ExtractValue<string>(inputMetadata, "vercel.chat.id")
                 ?? Guid.NewGuid().ToString("N"),
            Model = request.Model ?? string.Empty,
            Messages = outputMessages,
            ToolChoice = request.ToolChoice?.ToString(),
            MaxToolCalls = ExtractValue<int?>(metadata, "vercel.maxToolCalls")
                           ?? ExtractValue<int?>(inputMetadata, "vercel.maxToolCalls"),
            Temperature = request.Temperature ?? 1f,
            TopP = request.TopP is null ? null : (float?)request.TopP.Value,
            MaxOutputTokens = request.MaxOutputTokens,
            Tools = request.Tools?.Select(ToVercelTool).ToList() ?? [],
            ProviderMetadata = ExtractObject<Dictionary<string, JsonElement>>(metadata, "vercel.providerMetadata")
                               ?? ExtractObject<Dictionary<string, JsonElement>>(inputMetadata, "vercel.providerMetadata"),
            ResponseFormat = ExtractObject<object>(metadata, "vercel.responseFormat")
                             ?? ExtractObject<object>(inputMetadata, "vercel.responseFormat")
        };
    }

    private static AIToolDefinition ToUnifiedTool(Tool tool)
        => new()
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Metadata = new Dictionary<string, object?>
            {
                ["vercel.title"] = tool.Title
            }
        };

    private static Tool ToVercelTool(AIToolDefinition tool)
        => new()
        {
            Name = tool.Name,
            Description = tool.Description,
            Title = ExtractValue<string>(tool.Metadata, "vercel.title"),
            InputSchema = ConvertToToolInputSchema(tool.InputSchema)
        };

    private UIMessage? ToUIMessageFromInput(AIInputItem item)
    {
        if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            return null;

        var outputItem = new AIOutputItem
        {
            Type = "message",
            Role = item.Role,
            Content = item.Content,
            Metadata = item.Metadata
        };

        var id = ExtractValue<string>(item.Metadata, "vercel.message.id") ?? Guid.NewGuid().ToString("N");
        return _uiMapper.ToUIMessage(outputItem, id);
    }

    private static ToolInputSchema? ConvertToToolInputSchema(object? schema)
    {
        if (schema is null)
            return null;

        if (schema is ToolInputSchema typed)
            return typed;

        try
        {
            if (schema is JsonElement json)
                return JsonSerializer.Deserialize<ToolInputSchema>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<ToolInputSchema>(JsonSerializer.Serialize(schema, Json), Json);
        }
        catch
        {
            return null;
        }
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static T? ExtractValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }*/
}

