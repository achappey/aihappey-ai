using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    private const string AgentEndpoint = "v1/agent";
    private static readonly JsonSerializerOptions AgentJson = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();
        var native = BuildAgentRequest(request, false);
        using var message = CreateAgentHttpRequest(native, false);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureAgentSuccess(response, raw);
        using var document = JsonDocument.Parse(raw);
        return MapAgentResponse(request, document.RootElement);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();
        var native = BuildAgentRequest(request, true);
        using var message = CreateAgentHttpRequest(native, true);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            EnsureAgentSuccess(response, error);
        }

        var id = request.Id ?? Guid.NewGuid().ToString("N");
        var started = false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0)
                continue;
            if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            ThrowAgentPayloadError(root);
            var type = GetString(root, "type")?.ToLowerInvariant();
            if (type == "delta")
            {
                var text = GetString(root, "text") ?? string.Empty;
                if (!started)
                {
                    started = true;
                    yield return CreateAgentEvent(id, "text-start", new AITextStartEventData());
                }
                if (text.Length > 0)
                    yield return CreateAgentEvent(id, "text-delta", new AITextDeltaEventData { Delta = text });
                continue;
            }

            if (type == "text")
            {
                var text = GetString(root, "text") ?? string.Empty;
                if (!started)
                {
                    started = true;
                    yield return CreateAgentEvent(id, "text-start", new AITextStartEventData());
                }
                if (text.Length > 0)
                    yield return CreateAgentEvent(id, "text-delta", new AITextDeltaEventData { Delta = text });
                continue;
            }

            if (TryCreateFile(root, out var file))
            {
                yield return CreateAgentEvent(id, "file", new AIFileEventData
                {
                    MediaType = file.MediaType!,
                    Filename = file.Filename,
                    Url = ToFileUrl(file.Data, file.MediaType!)
                });
            }
            else
            {
                yield return CreateAgentEvent(id, "data", new AIDataEventData { Id = id, Data = root.Clone() });
            }
        }

        if (started)
            yield return CreateAgentEvent(id, "text-end", new AITextEndEventData());
        yield return CreateAgentEvent(id, "finish", new AIFinishEventData
        {
            FinishReason = "stop",
            Model = native.Model,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(native.Model, DateTimeOffset.UtcNow, temperature: request.Temperature)
        });
    }

    private static MimicXAgentRequest BuildAgentRequest(AIRequest request, bool stream)
    {
        if (request.Tools is { Count: > 0 } || request.ToolChoice is not null)
            throw new NotSupportedException("MIMICXAI does not accept explicit client tools or tool_choice; Nova selects registered skills from the prompt.");

        var model = NormalizeAgentModel(request.Model);
        var messages = new List<MimicXAgentMessage>();
        string? image = null;
        foreach (var item in request.Input?.Items ?? [])
        {
            var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>()
                .Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(text))
                messages.Add(new MimicXAgentMessage { Role = NormalizeAgentRole(item.Role), Content = text });

            foreach (var file in (item.Content ?? []).OfType<AIFileContentPart>())
            {
                if (file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    throw new NotSupportedException($"MIMICXAI agent input supports image files only; received '{file.MediaType ?? "unknown"}'.");
                if (image is not null)
                    throw new NotSupportedException("MIMICXAI agent accepts only one input image per request.");
                image = ExtractBase64(file.Data);
            }
        }

        var direct = request.Input?.Text;
        var prompt = !string.IsNullOrWhiteSpace(direct)
            ? direct!
            : messages.LastOrDefault(message => message.Role == "user")?.Content
              ?? messages.LastOrDefault()?.Content
              ?? string.Empty;
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("MIMICXAI requires a non-empty text prompt.", nameof(request));
        if (image is not null && model != "Nova")
            throw new NotSupportedException("MIMICXAI image input requires the Nova model.");

        return new MimicXAgentRequest
        {
            Model = model,
            Prompt = prompt,
            Stream = stream,
            Messages = messages.Count == 0 ? null : messages,
            SystemPrompt = request.Instructions,
            ImageBase64 = image,
            Temperature = request.Temperature,
            MaxTokens = request.MaxOutputTokens
        };
    }

    private static string NormalizeAgentModel(string? model)
    {
        var value = model?.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("darwin", StringComparison.OrdinalIgnoreCase)) return "Darwin";
        if (value.Equals("nova", StringComparison.OrdinalIgnoreCase)) return "Nova";
        throw new NotSupportedException($"MIMICXAI model '{model}' is unsupported. Use Darwin or Nova.");
    }

    private static string NormalizeAgentRole(string? role)
        => role?.ToLowerInvariant() switch { "assistant" => "assistant", "system" => "system", "developer" => "system", _ => "user" };

    private static string ExtractBase64(object? data)
    {
        var value = data switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => throw new NotSupportedException("MIMICXAI image data must be byte[], base64 text, or a data URL.")
        };
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? value[(marker + 8)..] : value;
    }

    private static HttpRequestMessage CreateAgentHttpRequest(MimicXAgentRequest request, bool stream)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, AgentEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, AgentJson), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        return message;
    }

    private AIResponse MapAgentResponse(AIRequest request, JsonElement root)
    {
        ThrowAgentPayloadError(root);
        var type = GetString(root, "type")?.ToLowerInvariant() ?? "text";
        var items = new List<AIOutputItem>();
        if (type == "text")
            items.Add(new AIOutputItem
            {
                Role = "assistant",
                Content = [new
            AITextContentPart {
                Type = "text",
                Text = GetString(root, "text") ?? string.Empty }]
            });
        else if (TryCreateFile(root, out var file))
            items.Add(new AIOutputItem { Type = "file", Content = [file] });
        else if (type == "job")
            items.Add(new AIOutputItem { Type = "job", Metadata = new Dictionary<string, object?> { ["mimicx.job"] = root.Clone() } });
        else
            throw new InvalidOperationException($"MIMICXAI returned unsupported result type '{type}'.");

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = GetString(root, "model") ?? NormalizeAgentModel(request.Model),
            Status = "completed",
            Output = new AIOutput { Items = items },
            Metadata = new Dictionary<string, object?> { ["mimicx.type"] = type, ["mimicx.raw"] = root.Clone() }
        };
    }

    private static bool TryCreateFile(JsonElement root, out AIFileContentPart file)
    {
        var type = GetString(root, "type")?.ToLowerInvariant();
        var spec = type switch
        {
            "image" => ("image_b64", "image/png", "output.png"),
            "video" => ("video_b64", "video/mp4", "output.mp4"),
            "audio" => ("audio_b64", "audio/mpeg", "output.mp3"),
            "model3d" => (root.TryGetProperty("model_b64", out _) ? "model_b64" : "model_url", "model/gltf-binary", "output.glb"),
            _ => default
        };
        if (spec.Item1 is null || !root.TryGetProperty(spec.Item1, out var data)) { file = null!; return false; }
        file = new AIFileContentPart
        {
            Type = "file",
            MediaType = spec.Item2,
            Filename = spec.Item3,
            Data = data.ValueKind == JsonValueKind.String ? data.GetString() : data.Clone()
        };
        return true;
    }

    private static string ToFileUrl(object? data, string mediaType)
    {
        var value = Convert.ToString(data) ?? string.Empty;
        return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : $"data:{mediaType};base64,{value}";
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void ThrowAgentPayloadError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MIMICXAI agent error: {(error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText())}");
        if (root.TryGetProperty("detail", out var detail) && GetString(root, "type") is null)
            throw new InvalidOperationException($"MIMICXAI agent error: {(detail.ValueKind == JsonValueKind.String ? detail.GetString() : detail.GetRawText())}");
    }

    private static void EnsureAgentSuccess(HttpResponseMessage response, string raw)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MIMICXAI agent request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
    }

    private AIStreamEvent CreateAgentEvent(string id, string type, object data) => new()
    {
        ProviderId = GetIdentifier(),
        Event = new AIEventEnvelope { Id = id, Type = type, Timestamp = DateTimeOffset.UtcNow, Data = data }
    };

    private sealed class MimicXAgentRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("prompt")] public required string Prompt { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("messages")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<MimicXAgentMessage>? Messages { get; init; }
        [JsonPropertyName("system_prompt")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SystemPrompt { get; init; }
        [JsonPropertyName("image_b64")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ImageBase64 { get; init; }
        [JsonPropertyName("temperature")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public float? Temperature { get; init; }
        [JsonPropertyName("max_tokens")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MaxTokens { get; init; }
    }

    private sealed class MimicXAgentMessage
    {
        [JsonPropertyName("role")] public required string Role { get; init; }
        [JsonPropertyName("content")] public required string Content { get; init; }
    }
}
