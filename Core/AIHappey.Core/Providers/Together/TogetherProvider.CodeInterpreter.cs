using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider
{
    internal const string CodeInterpreterModelSlug = "code-interpreter";

    private static readonly JsonSerializerOptions CodeInterpreterJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static bool IsCodeInterpreterModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var separator = model.IndexOf('/');
        var localModel = separator >= 0 ? model[(separator + 1)..] : model;
        return string.Equals(localModel, CodeInterpreterModelSlug, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AIResponse> ExecuteUnifiedCodeInterpreterAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var execution = await ExecuteCodeInterpreterAsync(request, cancellationToken);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model ?? $"{GetIdentifier()}/{CodeInterpreterModelSlug}",
            Status = "completed",
            Output = CreateCodeInterpreterOutput(execution.Text, execution.Metadata),
            Metadata = execution.Metadata
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamUnifiedCodeInterpreterAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteUnifiedCodeInterpreterAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;
        var id = $"together-code-{request.Id ?? Guid.NewGuid().ToString("N")}";
        var providerMetadata = CreateLooseCodeInterpreterMetadata(response.Metadata);

        yield return CreateCodeInterpreterEvent(
            "text-start", id, new AITextStartEventData { ProviderMetadata = providerMetadata }, response.Metadata);
        if (text.Length > 0)
        {
            yield return CreateCodeInterpreterEvent(
                "text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, response.Metadata);
        }
        yield return CreateCodeInterpreterEvent(
            "text-end", id, new AITextEndEventData { ProviderMetadata = providerMetadata }, response.Metadata);
        yield return CreateCodeInterpreterEvent(
            "finish",
            id,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? $"{GetIdentifier()}/{CodeInterpreterModelSlug}",
                    DateTimeOffset.UtcNow,
                    temperature: request.Temperature)
            },
            response.Metadata,
            response.Output);
    }

    private async Task<(string Text, Dictionary<string, object?> Metadata)> ExecuteCodeInterpreterAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var code = ExtractLatestUserCode(request);
        var payload = ExtractCodeInterpreterProviderOptions(request.Metadata);
        payload["code"] = code;
        if (!payload.TryGetValue("language", out var language)
            || language is null
            || language is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            payload["language"] = "python";
        }

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tci/execute")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CodeInterpreterJson),
                Encoding.UTF8,
                "application/json")
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together Code Interpreter failed ({(int)response.StatusCode}): {raw}");

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(raw);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Together Code Interpreter returned invalid JSON.", ex);
        }

        if (root.TryGetProperty("errors", out var errors)
            && errors.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Together Code Interpreter execution failed: {errors.GetRawText()}");
        }

        var text = FormatCodeInterpreterOutputs(root);
        var metadata = new Dictionary<string, object?>
        {
            ["providerMetadata"] = new Dictionary<string, object?>
            {
                [GetIdentifier()] = root
            }
        };
        return (text, metadata);
    }

    private static string ExtractLatestUserCode(AIRequest request)
    {
        var latestUser = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var code = string.Join("\n", latestUser?.Content?
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)) ?? []);

        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Together Code Interpreter requires text in the latest user message.", nameof(request));
        return code;
    }

    private Dictionary<string, object?> ExtractCodeInterpreterProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return [];

        object? scoped = null;
        if (metadata.TryGetValue(GetIdentifier(), out var direct))
            scoped = direct;
        else if (metadata.TryGetValue("providerMetadata", out var providerMetadata))
            scoped = TryGetScopedProviderValue(providerMetadata, GetIdentifier());

        return ToObjectMap(scoped);
    }

    private static object? TryGetScopedProviderValue(object? value, string providerId)
    {
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return element.TryGetProperty(providerId, out var scoped) ? scoped.Clone() : null;
        if (value is IDictionary<string, object?> nullableMap)
            return nullableMap.TryGetValue(providerId, out var scoped) ? scoped : null;
        if (value is IDictionary<string, object> map)
            return map.TryGetValue(providerId, out var scoped) ? scoped : null;
        return null;
    }

    private static Dictionary<string, object?> ToObjectMap(object? value)
    {
        if (value is null)
            return [];
        if (value is Dictionary<string, object?> map)
            return new Dictionary<string, object?>(map, StringComparer.OrdinalIgnoreCase);

        var element = value is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(value, CodeInterpreterJson);
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => (object?)property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatCodeInterpreterOutputs(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("outputs", out var outputs)
            || outputs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Together Code Interpreter response did not contain data.outputs.");
        }

        return string.Join("\n", outputs.EnumerateArray().Select(FormatCodeInterpreterOutput));
    }

    private static string FormatCodeInterpreterOutput(JsonElement output)
    {
        if (!output.TryGetProperty("data", out var data))
            return output.GetRawText();
        if (data.ValueKind == JsonValueKind.String)
            return data.GetString() ?? string.Empty;
        if (data.ValueKind != JsonValueKind.Object)
            return data.GetRawText();

        foreach (var mimeType in new[] { "text/markdown", "text/plain", "application/json" })
        {
            if (!data.TryGetProperty(mimeType, out var preferred))
                continue;
            return preferred.ValueKind == JsonValueKind.String
                ? preferred.GetString() ?? string.Empty
                : preferred.GetRawText();
        }
        return data.GetRawText();
    }

    private static AIOutput CreateCodeInterpreterOutput(string text, Dictionary<string, object?> metadata)
        => new()
        {
            Items =
            [
                new AIOutputItem
                {
                    Type = "message",
                    Role = "assistant",
                    Metadata = metadata,
                    Content = [new AITextContentPart { 
                        Type = "text",
                        Text = text, Metadata = metadata }]
                }
            ],
            Metadata = metadata
        };

    private static Dictionary<string, object>? CreateLooseCodeInterpreterMetadata(Dictionary<string, object?>? metadata)
        => metadata?.Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);

    private AIStreamEvent CreateCodeInterpreterEvent(
        string type,
        string id,
        object data,
        Dictionary<string, object?>? metadata,
        AIOutput? output = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = DateTimeOffset.UtcNow,
                Data = data,
                Output = output,
                Metadata = metadata
            }
        };
}
