using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Zebracat;

public partial class ZebracatProvider
{
    private const string ScriptGeneratorModel = "script-generator";
    private static readonly JsonSerializerOptions ZebracatScriptJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<AIResponse> ExecuteScriptGeneratorUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = NormalizeZebracatModel(request.Model ?? string.Empty);
        if (!string.Equals(model, ScriptGeneratorModel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported Zebracat text model '{request.Model}'.", nameof(request));

        var payload = CopyZebracatScriptOptions(
            request.Metadata.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["idea"] = GetLatestZebracatUserText(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/script_generator")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ZebracatScriptJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        ApplyAuthHeader(httpRequest);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Zebracat script generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var script = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("script", out var scriptElement)
            && scriptElement.ValueKind == JsonValueKind.String
                ? scriptElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException("Zebracat script generation returned no script.");

        var modelId = request.Model ?? $"{GetIdentifier()}/{ScriptGeneratorModel}";
        var metadata = new Dictionary<string, object?>
        {
            ["finishReason"] = "stop",
            ["zebracat.response.raw"] = root.Clone()
        };

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Status = "completed",
            Usage = new Dictionary<string, object?>(),
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = script }],
                        Metadata = metadata
                    }
                ],
                Metadata = metadata
            }
        };
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamScriptGeneratorUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await ExecuteScriptGeneratorUnifiedAsync(request, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .FirstOrDefault()?.Text ?? string.Empty;
        var eventId = Guid.NewGuid().ToString("n");
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateZebracatTextEvent(
            eventId, "text-start", new AITextStartEventData(), timestamp, response.Metadata);
        if (!string.IsNullOrEmpty(text))
            yield return CreateZebracatTextEvent(
                eventId, "text-delta", new AITextDeltaEventData { Delta = text }, timestamp, response.Metadata);
        yield return CreateZebracatTextEvent(
            eventId, "text-end", new AITextEndEventData(), timestamp, response.Metadata);
        yield return CreateZebracatTextEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = response.Model,
                CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    response.Model ?? $"{GetIdentifier()}/{ScriptGeneratorModel}",
                    DateTimeOffset.UtcNow,
                    response.Usage as Dictionary<string, object?>,
                    temperature: request.Temperature)
            },
            DateTimeOffset.UtcNow,
            response.Metadata);
    }

    private AIStreamEvent CreateZebracatTextEvent(
        string id,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Id = id,
                Type = type,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            }
        };

    private static Dictionary<string, object?> CopyZebracatScriptOptions(JsonElement options)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in options.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private static string GetLatestZebracatUserText(AIRequest request)
    {
        if (request.Input?.Items is { Count: > 0 })
        {
            for (var index = request.Input.Items.Count - 1; index >= 0; index--)
            {
                var item = request.Input.Items[index];
                if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                    continue;

                if ((item.Content ?? []).Any(part => part is not AITextContentPart))
                    throw new NotSupportedException("Zebracat script generation supports text content only.");

                var textParts = (item.Content ?? [])
                    .OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                if (textParts.Count > 0)
                    return string.Join("\n", textParts);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;

        throw new ArgumentException(
            "Zebracat script generation requires text in the latest user message.",
            nameof(request));
    }
}
