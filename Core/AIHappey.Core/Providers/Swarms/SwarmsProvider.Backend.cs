using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Swarms;

public partial class SwarmsProvider
{
    private static readonly JsonSerializerOptions SwarmsJson = JsonSerializerOptions.Web;
    private const string SwarmsModelsCacheSuffix = ":models-available";

    private sealed class SwarmsAvailableModelsEnvelope
    {
        public bool? Success { get; set; }
        public JsonElement Models { get; set; }
    }

    private sealed class SwarmsBackendModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string OwnedBy { get; set; } = nameof(Swarms);
        public string Type { get; set; } = "language";
        public int? ContextWindow { get; set; }
        public int? MaxTokens { get; set; }
        public ModelPricing? Pricing { get; set; }
        public IEnumerable<string>? Tags { get; set; }
    }

    private sealed record SwarmsResolvedModel(
        string ExposedModelId,
        string BackendModelId,
        string BackendModelName);

    private sealed class SwarmsCompletionRequest
    {
        public SwarmsAgentConfig Agent_Config { get; set; } = new();
        public string? Task { get; set; }
        public object? History { get; set; }
    }

    private sealed class SwarmsAgentConfig
    {
        public string? Agent_Name { get; set; }
        public string? Description { get; set; }
        public string? System_Prompt { get; set; }
        public string? Model_Name { get; set; }
        public double? Temperature { get; set; }
        public int? Max_Tokens { get; set; }
        public bool? Streaming_On { get; set; }
    }

    private sealed class SwarmsCompletionResponse
    {
        public string? Job_Id { get; set; }
        public bool? Success { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public double? Temperature { get; set; }
        public JsonElement Outputs { get; set; }
        public object? Usage { get; set; }
        public string? Timestamp { get; set; }
    }

    private sealed record SwarmsExecutionResult(
        string Id,
        string Model,
        string Text,
        long CreatedAt,
        long CompletedAt,
        object? Usage,
        JsonElement Raw);

    private async Task<IReadOnlyList<SwarmsBackendModel>> GetAvailableBackendModelsAsync(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IReadOnlyList<SwarmsBackendModel>>([]);

        var cacheKey = this.GetCacheKey(key) + SwarmsModelsCacheSuffix;

        var models = await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async _ =>
            {
                ApplyAuthHeader();
                using var response = await _client.GetAsync("v1/models/available", cancellationToken);
                var raw = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Swarms models error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

                var envelope = JsonSerializer.Deserialize<SwarmsAvailableModelsEnvelope>(raw, SwarmsJson)
                    ?? new SwarmsAvailableModelsEnvelope();

                return ParseBackendModels(envelope.Models).ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

        return models is IReadOnlyList<SwarmsBackendModel> list ? list : models.ToList();
    }

    private async Task<SwarmsResolvedModel> ResolveModelAsync(string exposedModelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exposedModelId);

        var (_, backendModelId) = exposedModelId.SplitModelId();

        var models = await GetAvailableBackendModelsAsync(cancellationToken);
        var model = models.FirstOrDefault(m => string.Equals(m.Id, backendModelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException($"Swarms backend model '{backendModelId}' was not found.");

        return new SwarmsResolvedModel(
            exposedModelId,
            model.Id,
            model.Name);
    }

    private async Task<SwarmsExecutionResult> ExecuteCompletionAsync(
        string exposedModelId,
        string prompt,
        object? history,
        string? systemPrompt,
        double? temperature,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveModelAsync(exposedModelId, cancellationToken);
        var request = BuildCompletionRequest(resolved, prompt, history, systemPrompt, temperature, maxTokens, streaming: false);

        ApplyAuthHeader();
        using var response = await _client.PostAsJsonAsync("v1/agent/completions", request, SwarmsJson, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Swarms completion error: {(int)response.StatusCode} {response.ReasonPhrase}: {raw}");

        var completion = JsonSerializer.Deserialize<SwarmsCompletionResponse>(raw, SwarmsJson)
            ?? throw new InvalidOperationException("Swarms returned an empty completion response.");

        var text = ExtractCompletionText(completion.Outputs);
        var createdAt = ParseUnixTime(completion.Timestamp);

        using var document = JsonDocument.Parse(raw);
        return new SwarmsExecutionResult(
            completion.Job_Id ?? Guid.NewGuid().ToString("n"),
            exposedModelId,
            text,
            createdAt,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            completion.Usage,
            document.RootElement.Clone());
    }

    private async IAsyncEnumerable<string> ExecuteCompletionStreamingAsync(
        string exposedModelId,
        string prompt,
        object? history,
        string? systemPrompt,
        double? temperature,
        int? maxTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var executed = await ExecuteCompletionAsync(exposedModelId, prompt, history, systemPrompt, temperature, maxTokens, cancellationToken);
        if (!string.IsNullOrWhiteSpace(executed.Text))
            yield return executed.Text;
    }

    private static SwarmsCompletionRequest BuildCompletionRequest(
        SwarmsResolvedModel resolved,
        string prompt,
        object? history,
        string? systemPrompt,
        double? temperature,
        int? maxTokens,
        bool streaming)
        => new()
        {
            Task = prompt,
            History = history,
            Agent_Config = new SwarmsAgentConfig
            {
                Agent_Name = resolved.BackendModelName,
                Description = $"AIHappey Swarms request for backend model '{resolved.BackendModelName}'.",
                System_Prompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                Model_Name = resolved.BackendModelId,
                Temperature = temperature,
                Max_Tokens = maxTokens,
                Streaming_On = streaming
            }
        };

    private static string? ExtractSystemPrompt(IEnumerable<ChatMessage> messages)
    {
        var combined = string.Join("\n\n", (messages ?? [])
            .Where(m => string.Equals(m.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static string? ExtractSystemPrompt(IEnumerable<UIMessage> messages)
    {
        var combined = string.Join("\n\n", (messages ?? [])
            .Where(m => string.Equals(m.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => string.Join("\n", m.Parts.OfType<TextUIPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t))))
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static string? ExtractSystemPrompt(ResponseRequest request)
    {
        var systemMessages = request.Input?.Items?
            .OfType<ResponseInputMessage>()
            .Where(m => string.Equals(m.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content.IsText
                ? m.Content.Text
                : string.Join("\n", m.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        if (systemMessages is { Count: > 0 })
            return string.Join("\n\n", systemMessages);

        return string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions;
    }

    private static IEnumerable<SwarmsBackendModel> ParseBackendModels(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataEl))
            root = dataEl;

        if (root.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var stringModelId = item.GetString();
                if (string.IsNullOrWhiteSpace(stringModelId))
                    continue;

                yield return new SwarmsBackendModel
                {
                    Id = stringModelId,
                    Name = stringModelId,
                    OwnedBy = nameof(Swarms),
                    Type = stringModelId.GuessModelType()
                };

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = TryGetString(item, "id") ?? TryGetString(item, "model_name") ?? TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = TryGetString(item, "name") ?? id;
            var inputPrice = ReadDecimal(item, "input_price") ?? ReadNestedDecimal(item, "pricing", "input");
            var outputPrice = ReadDecimal(item, "output_price") ?? ReadNestedDecimal(item, "pricing", "output");

            yield return new SwarmsBackendModel
            {
                Id = id,
                Name = name,
                OwnedBy = TryGetString(item, "owned_by") ?? nameof(Swarms),
                Type = TryGetString(item, "type") ?? id.GuessModelType(),
                ContextWindow = ReadInt(item, "context_window") ?? ReadInt(item, "context_length"),
                MaxTokens = ReadInt(item, "max_tokens") ?? ReadInt(item, "output_tokens"),
                Pricing = inputPrice is not null && outputPrice is not null
                    ? new ModelPricing { Input = inputPrice.Value, Output = outputPrice.Value }
                    : null,
                Tags = ReadTags(item)
            };
        }
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            if (string.Equals(message.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = ChatMessageContentExtensions.ToText(message.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            if (string.Equals(message.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join("\n", message.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static object? BuildHistoryFromChatMessages(IEnumerable<ChatMessage> messages)
    {
        var history = messages?
            .Where(m => !string.Equals(m.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new Dictionary<string, string>
            {
                ["role"] = m.Role.ToString(),
                ["content"] = ChatMessageContentExtensions.ToText(m.Content) ?? string.Empty
            })
            .Where(m => !string.IsNullOrWhiteSpace(m["content"]))
            .Cast<object>()
            .ToList();

        return history is { Count: > 0 } ? history : null;
    }

    private static object? BuildHistoryFromUiMessages(IEnumerable<UIMessage> messages)
    {
        var history = messages?
            .Where(m => !string.Equals(m.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new Dictionary<string, string>
            {
                ["role"] = m.Role.ToString(),
                ["content"] = string.Join("\n", m.Parts.OfType<TextUIPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)))
            })
            .Where(m => !string.IsNullOrWhiteSpace(m["content"]))
            .Cast<object>()
            .ToList();

        return history is { Count: > 0 } ? history : null;
    }

    private static object? BuildHistoryFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsItems != true || request.Input.Items is null)
            return null;

        var history = new List<object>();
        foreach (var item in request.Input.Items.OfType<ResponseInputMessage>())
        {
            if (string.Equals(item.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = item.Content.IsText
                ? item.Content.Text
                : string.Join("\n", item.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            history.Add(new Dictionary<string, string>
            {
                ["role"] = item.Role.ToString().ToLowerInvariant(),
                ["content"] = text
            });
        }

        return history.Count > 0 ? history : null;
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsText == true)
            return request.Input.Text ?? string.Empty;

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            var lines = new List<string>();
            foreach (var message in request.Input.Items.OfType<ResponseInputMessage>())
            {
                if (string.Equals(message.Role.ToString(), "system", StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?.OfType<InputTextPart>().Select(p => p.Text) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{message.Role.ToString().ToLowerInvariant()}: {text}");
            }

            if (lines.Count > 0)
                return string.Join("\n\n", lines);
        }

        return request.Instructions ?? string.Empty;
    }

    private static string ExtractCompletionText(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.String)
            return output.GetString() ?? string.Empty;

        if (output.ValueKind == JsonValueKind.Array)
        {
            var chunks = output.EnumerateArray()
                .Select(ExtractCompletionText)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            return chunks.Count > 0 ? string.Join("\n", chunks) : output.ToString();
        }

        if (output.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in new[] { "text", "content", "message", "response", "output", "result" })
            {
                if (output.TryGetProperty(property, out var prop))
                {
                    var text = ExtractCompletionText(prop);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            if (output.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    var text = ExtractCompletionText(choice);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            if (output.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var text = ExtractCompletionText(message);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
        }

        return output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : output.ToString();
    }

    private static string ExtractOutputText(IEnumerable<object> output)
    {
        foreach (var element in output.Select(o => JsonSerializer.SerializeToElement(o, SwarmsJson)))
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (!element.TryGetProperty("type", out var typeEl)
                || !string.Equals(typeEl.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    return textEl.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static ResponseResult ToResponseResult(SwarmsExecutionResult executed, ResponseRequest request)
        => new()
        {
            Id = executed.Id,
            Object = "response",
            CreatedAt = executed.CreatedAt,
            CompletedAt = executed.CompletedAt,
            Status = "completed",
            Model = request.Model ?? executed.Model,
            Temperature = request.Temperature,
            Metadata = request.Metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = executed.Usage,
            Output =
            [
                new
                {
                    id = $"msg_{executed.Id}",
                    type = "message",
                    role = "assistant",
                    status = "completed",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = executed.Text,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };

    private static long ParseUnixTime(string? timestamp)
    {
        if (DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToUnixTimeSeconds();

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static IEnumerable<string>? ReadTags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tagsEl))
            return null;

        return tagsEl.ValueKind switch
        {
            JsonValueKind.Array => tagsEl.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray(),
            JsonValueKind.String => tagsEl.GetString()?
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            _ => null
        };
    }

    private static string? TryGetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static decimal? ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static decimal? ReadNestedDecimal(JsonElement element, string property, string nestedProperty)
    {
        if (!element.TryGetProperty(property, out var outer) || outer.ValueKind != JsonValueKind.Object)
            return null;

        return ReadDecimal(outer, nestedProperty);
    }
}
