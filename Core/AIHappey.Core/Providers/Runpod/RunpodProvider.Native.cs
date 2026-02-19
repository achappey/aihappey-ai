using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    private static readonly JsonSerializerOptions RunpodJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<JsonDocument> RunSyncAdaptiveAsync(
        string? model,
        IEnumerable<object> messages,
        float? temperature,
        int? maxTokens,
        float? topP,
        int? topK,
        CancellationToken cancellationToken)
    {
        var modelId = NormalizeRunpodModelId(model);
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(model));

        var route = BuildRunSyncRoute(modelId);

        var attemptA = BuildMessagesPayload(messages, temperature, maxTokens, topP, topK);
        var respA = await PostRunpodAsync(route, attemptA, cancellationToken).ConfigureAwait(false);
        if (respA.IsSuccessStatusCode)
            return await ParseRunpodDocAsync(respA, cancellationToken).ConfigureAwait(false);

        var bodyA = await respA.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var prompt = BuildPrompt(messages);
        var attemptB = BuildPromptPayload(prompt, temperature, maxTokens, topP, topK);
        var respB = await PostRunpodAsync(route, attemptB, cancellationToken).ConfigureAwait(false);
        if (respB.IsSuccessStatusCode)
            return await ParseRunpodDocAsync(respB, cancellationToken).ConfigureAwait(false);

        var bodyB = await respB.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        throw new HttpRequestException(
            $"Runpod native request failed for model '{modelId}'. First attempt: {(int)respA.StatusCode} {respA.ReasonPhrase}: {bodyA}. " +
            $"Second attempt: {(int)respB.StatusCode} {respB.ReasonPhrase}: {bodyB}");
    }

    private async Task<JsonDocument> GetRunpodStatusAsync(
        string? model,
        string jobId,
        CancellationToken cancellationToken)
    {
        var modelId = NormalizeRunpodModelId(model);
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(model));

        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));

        using var resp = await _client.GetAsync($"{modelId}/status/{jobId}", cancellationToken).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Runpod status error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        return JsonDocument.Parse(raw);
    }

    private async Task<(string jobId, JsonDocument? completed)> SubmitRunAsync(
        string? model,
        IEnumerable<object> messages,
        float? temperature,
        int? maxTokens,
        float? topP,
        int? topK,
        CancellationToken cancellationToken)
    {
        var modelId = NormalizeRunpodModelId(model);
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model is required.", nameof(model));

        var route = BuildRunRoute(modelId);
        var attemptA = BuildMessagesPayload(messages, temperature, maxTokens, topP, topK);

        using var respA = await PostRunpodAsync(route, attemptA, cancellationToken).ConfigureAwait(false);
        var rawA = await respA.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (respA.IsSuccessStatusCode)
            return ParseRunSubmission(rawA);

        var prompt = BuildPrompt(messages);
        var attemptB = BuildPromptPayload(prompt, temperature, maxTokens, topP, topK);
        using var respB = await PostRunpodAsync(route, attemptB, cancellationToken).ConfigureAwait(false);
        var rawB = await respB.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (respB.IsSuccessStatusCode)
            return ParseRunSubmission(rawB);

        throw new HttpRequestException(
            $"Runpod /run failed for model '{modelId}'. First attempt: {(int)respA.StatusCode} {respA.ReasonPhrase}: {rawA}. " +
            $"Second attempt: {(int)respB.StatusCode} {respB.ReasonPhrase}: {rawB}");
    }

    private static (string Text, int? PromptTokens, int? CompletionTokens) ExtractRunpodTextAndUsage(JsonElement root)
    {
        var text = string.Empty;
        int? promptTokens = null;
        int? completionTokens = null;

        if (root.TryGetProperty("output", out var outputEl))
        {
            if (outputEl.ValueKind == JsonValueKind.Object)
            {
                if (outputEl.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Number)
                        promptTokens = inputEl.GetInt32();
                    if (usageEl.TryGetProperty("output", out var outEl) && outEl.ValueKind == JsonValueKind.Number)
                        completionTokens = outEl.GetInt32();
                }

                text = ExtractTextFromOutputObject(outputEl);
            }
            else if (outputEl.ValueKind == JsonValueKind.Array)
            {
                var first = outputEl.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                    text = ExtractTextFromOutputObject(first);
            }
        }

        return (text, promptTokens, completionTokens);
    }

    private static string ExtractTextFromOutputObject(JsonElement outputEl)
    {
        if (outputEl.TryGetProperty("choices", out var choicesEl)
            && choicesEl.ValueKind == JsonValueKind.Array)
        {
            var firstChoice = choicesEl.EnumerateArray().FirstOrDefault();
            if (firstChoice.ValueKind == JsonValueKind.Object)
            {
                if (firstChoice.TryGetProperty("tokens", out var tokensEl)
                    && tokensEl.ValueKind == JsonValueKind.Array)
                {
                    var joined = string.Concat(tokensEl.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String)
                        .Select(t => t.GetString()));

                    if (!string.IsNullOrWhiteSpace(joined))
                        return joined;
                }

                if (firstChoice.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    return textEl.GetString() ?? string.Empty;
            }
        }

        if (outputEl.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
            return directText.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static IEnumerable<object> ToRunpodMessages(ChatRequest request)
        => request.Messages
            .SelectMany(m => m.Parts
                .OfType<TextUIPart>()
                .Select(p => new
                {
                    role = m.Role.ToString(),
                    content = p.Text
                }))
            .Where(m => !string.IsNullOrWhiteSpace(m.content));

    private static IEnumerable<object> ToRunpodMessages(IEnumerable<ChatMessage> messages)
        => messages
            .Select(m => new
            {
                role = m.Role,
                content = ChatMessageContentExtensions.ToText(m.Content) ?? m.Content.GetRawText()
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.content));

    private static string BuildPrompt(IEnumerable<object> messages)
    {
        var lines = new List<string>();

        foreach (var msg in messages)
        {
            var el = JsonSerializer.SerializeToElement(msg, RunpodJson);
            if (!el.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
                continue;

            var content = contentEl.GetString();
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var role = el.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString()
                : "user";

            lines.Add($"{role}: {content}");
        }

        return string.Join("\n", lines);
    }

    private static Dictionary<string, object?> BuildMessagesPayload(
        IEnumerable<object> messages,
        float? temperature,
        int? maxTokens,
        float? topP,
        int? topK)
        => new()
        {
            ["input"] = new
            {
                messages = messages,
                sampling_params = new
                {
                    max_tokens = maxTokens,
                    temperature,
                    top_p = topP,
                    top_k = topK
                }
            }
        };

    private static Dictionary<string, object?> BuildPromptPayload(
        string prompt,
        float? temperature,
        int? maxTokens,
        float? topP,
        int? topK)
        => new()
        {
            ["input"] = new
            {
                prompt,
                max_tokens = maxTokens,
                temperature,
                top_p = topP,
                top_k = topK
            }
        };

    private async Task<HttpResponseMessage> PostRunpodAsync(
        string route,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, RunpodJson);
        var req = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        return await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ParseRunpodDocAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(raw);
    }

    private static (string jobId, JsonDocument? completed) ParseRunSubmission(string raw)
    {
        var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, doc);

        if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Runpod /run response missing id: {raw}");

        return (idEl.GetString() ?? string.Empty, null);
    }

    private static string NormalizeRunpodModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var normalized = model.Trim();
        if (normalized.Contains('/'))
            normalized = normalized.SplitModelId().Model;

        return normalized;
    }

    private static string BuildRunSyncRoute(string modelId) => $"{modelId}/runsync";
    private static string BuildRunRoute(string modelId) => $"{modelId}/run";

    private static ResponseResult BuildResponseResultFromRunpod(
        string id,
        string model,
        string text,
        int? promptTokens,
        int? completionTokens,
        ResponseRequest options,
        string status = "completed")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pt = promptTokens ?? 0;
        var ct = completionTokens ?? 0;

        return new ResponseResult
        {
            Id = id,
            Object = "response",
            CreatedAt = now,
            CompletedAt = now,
            Status = status,
            Model = model,
            Temperature = options.Temperature,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output =
            [
                new
                {
                    id = $"msg_{id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ],
            Usage = new
            {
                input_tokens = pt,
                output_tokens = ct,
                total_tokens = pt + ct
            }
        };
    }

    private static IEnumerable<object> ToRunpodMessages(ResponseRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new
            {
                role = "system",
                content = request.Instructions
            });
        }

        if (request.Input?.IsText == true && !string.IsNullOrWhiteSpace(request.Input.Text))
        {
            messages.Add(new
            {
                role = "user",
                content = request.Input.Text
            });

            return messages;
        }

        if (request.Input?.IsItems == true && request.Input.Items is { Count: > 0 })
        {
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage msg)
                    continue;

                var role = msg.Role switch
                {
                    ResponseRole.Assistant => "assistant",
                    ResponseRole.System => "system",
                    ResponseRole.Developer => "system",
                    _ => "user"
                };

                var text = msg.Content.IsText
                    ? msg.Content.Text
                    : string.Concat((msg.Content.Parts ?? [])
                        .OfType<InputTextPart>()
                        .Select(p => p.Text));

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                messages.Add(new
                {
                    role,
                    content = text
                });
            }
        }

        return messages;
    }

    private async Task<JsonDocument> WaitForRunCompletionAsync(
        string model,
        string jobId,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 120; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var statusDoc = await GetRunpodStatusAsync(model, jobId, cancellationToken).ConfigureAwait(false);
            var root = statusDoc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;

            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                return JsonDocument.Parse(root.GetRawText());

            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "TIMED_OUT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Runpod job {jobId} failed with status '{status}': {root.GetRawText()}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Runpod job {jobId} did not complete in time.");
    }

    private static HttpStatusCode ToStatusCode(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Number)
            return HttpStatusCode.OK;

        return (HttpStatusCode)element.Value.GetInt32();
    }
}

