using System.IO.Compression;
using System.Net;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Smooth;

public partial class SmoothProvider
{
    private static readonly JsonSerializerOptions SmoothJson = JsonSerializerOptions.Web;

    private async Task<ResponseResult> ExecuteResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Smooth requires non-empty input.");

        var created = await SubmitTaskAsync(options, prompt, cancellationToken);
        var eventT = 0L;
        var providerEventLog = new StringBuilder();
        SmoothTaskResponse current = created;

        while (!cancellationToken.IsCancellationRequested)
        {
            current = await GetTaskAsync(created.Id, eventT, downloads: true, cancellationToken);

            var eventResult = await HandleTaskEventsAsync(created.Id, current.Events, cancellationToken);
            eventT = Math.Max(eventT, eventResult.NextEventTimestamp);
            foreach (var line in eventResult.LogLines)
                providerEventLog.AppendLine(line);

            if (IsTerminal(current.Status))
                break;

            await Task.Delay(900, cancellationToken);
        }

        var downloadedImages = await DownloadImagesAsDataUrlsAsync(current.DownloadsUrl, cancellationToken);
        return ToResponseResult(current, options, providerEventLog, downloadedImages);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("Smooth requires non-empty input.");

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;

        var created = await SubmitTaskAsync(options, prompt, cancellationToken);
        var responseId = created.Id;
        var itemId = $"msg_{responseId}";
        var eventT = 0L;
        var textStarted = false;
        var emittedText = string.Empty;
        var providerEventLog = new StringBuilder();

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = options.Model ?? "smooth-agent",
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = await GetTaskAsync(created.Id, eventT, downloads: true, cancellationToken);

            var eventResult = await HandleTaskEventsAsync(created.Id, current.Events, cancellationToken);
            eventT = Math.Max(eventT, eventResult.NextEventTimestamp);

            foreach (var line in eventResult.LogLines)
            {
                providerEventLog.AppendLine(line);
                if (!textStarted)
                    textStarted = true;

                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = line + "\n"
                };
            }

            var currentText = ResolveFinalOutputText(current, providerEventLog);
            if (!string.IsNullOrWhiteSpace(currentText))
            {
                if (currentText.StartsWith(emittedText, StringComparison.Ordinal))
                {
                    var delta = currentText[emittedText.Length..];
                    if (!string.IsNullOrEmpty(delta))
                    {
                        textStarted = true;
                        emittedText = currentText;
                        yield return new ResponseOutputTextDelta
                        {
                            SequenceNumber = sequence++,
                            ItemId = itemId,
                            Outputindex = 0,
                            ContentIndex = 0,
                            Delta = delta
                        };
                    }
                }
                else
                {
                    textStarted = true;
                    emittedText = currentText;
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = sequence++,
                        ItemId = itemId,
                        Outputindex = 0,
                        ContentIndex = 0,
                        Delta = currentText
                    };
                }
            }

            if (IsTerminal(current.Status))
            {
                var downloadedImages = await DownloadImagesAsDataUrlsAsync(current.DownloadsUrl, cancellationToken);
                var finalResult = ToResponseResult(current, options, providerEventLog, downloadedImages);
                var finalText = ExtractOutputText(finalResult.Output);

                yield return new ResponseOutputTextDone
                {
                    SequenceNumber = sequence++,
                    ItemId = itemId,
                    Outputindex = 0,
                    ContentIndex = 0,
                    Text = finalText
                };

                if (IsFinished(current.Status))
                {
                    yield return new ResponseCompleted
                    {
                        SequenceNumber = sequence,
                        Response = finalResult
                    };
                }
                else
                {
                    yield return new ResponseFailed
                    {
                        SequenceNumber = sequence,
                        Response = finalResult
                    };
                }

                yield break;
            }

            await Task.Delay(900, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task<SmoothTaskResponse> SubmitTaskAsync(
        ResponseRequest options,
        string prompt,
        CancellationToken cancellationToken)
    {
        var request = new SmoothSubmitTaskRequest
        {
            Task = prompt,
            Agent = ResolveSmoothAgent(options.Model),
            MaxSteps = ClampSteps(options.MaxOutputTokens),
            ResponseModel = TryExtractStructuredOutputSchema(options.Text),
            CustomTools = ToSmoothToolSignatures(options.Tools)
        };

        var json = JsonSerializer.Serialize(request, SmoothJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/task")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Smooth submit task failed ({(int)resp.StatusCode}): {raw}");

        var model = JsonSerializer.Deserialize<SmoothApiResponse<SmoothTaskResponse>>(raw, SmoothJson)
                    ?? throw new InvalidOperationException("Smooth submit task returned empty payload.");

        return model.R;
    }

    private async Task<SmoothTaskResponse> GetTaskAsync(
        string taskId,
        long eventT,
        bool downloads,
        CancellationToken cancellationToken)
    {
        var route = $"api/v1/task/{Uri.EscapeDataString(taskId)}?event_t={eventT}&downloads={(downloads ? "true" : "false")}";

        using var req = new HttpRequestMessage(HttpMethod.Get, route);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Smooth get task failed ({(int)resp.StatusCode}): {raw}");

        var model = JsonSerializer.Deserialize<SmoothApiResponse<SmoothTaskResponse>>(raw, SmoothJson)
                    ?? throw new InvalidOperationException("Smooth get task returned empty payload.");

        return model.R;
    }

    private async Task SubmitToolCallEventAsync(
        string taskId,
        string sourceEventId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var responseEvent = new SmoothTaskEvent
        {
            Name = "tool_call",
            Id = $"evt_response_{sourceEventId}",
            Payload = JsonSerializer.SerializeToElement(new
            {
                code = 501,
                output = new
                {
                    handled_by = "aihappey.smooth_provider",
                    message = "Tool execution is not wired in this provider yet. Responding with provider-side fallback.",
                    source_event_id = sourceEventId,
                    original_payload = payload
                }
            }, SmoothJson)
        };

        var json = JsonSerializer.Serialize(responseEvent, SmoothJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/task/{Uri.EscapeDataString(taskId)}/event")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (resp.IsSuccessStatusCode)
            return;

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Smooth send event failed ({(int)resp.StatusCode}): {raw}");
    }

    private async Task<(long NextEventTimestamp, List<string> LogLines)> HandleTaskEventsAsync(
        string taskId,
        List<SmoothTaskEvent>? events,
        CancellationToken cancellationToken)
    {
        var nextEventTimestamp = 0L;
        var lines = new List<string>();

        foreach (var evt in events ?? [])
        {
            if (evt.Timestamp.HasValue)
                nextEventTimestamp = Math.Max(nextEventTimestamp, evt.Timestamp.Value);

            if (!string.Equals(evt.Name, "tool_call", StringComparison.OrdinalIgnoreCase))
                continue;

            var eventId = evt.Id ?? Guid.NewGuid().ToString("n");
            var toolName = ExtractEventToolName(evt.Payload) ?? eventId;
            lines.Add($"[tool_call] Smooth requested tool '{toolName}'. Responding with provider fallback.");

            await SubmitToolCallEventAsync(taskId, eventId, evt.Payload, cancellationToken);
        }

        return (nextEventTimestamp, lines);
    }

    private async Task<List<SmoothDownloadedImage>> DownloadImagesAsDataUrlsAsync(
        string? downloadsUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(downloadsUrl))
            return [];

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadsUrl);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return [];

            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return [];

            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
            var result = new List<SmoothDownloadedImage>();

            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                var mediaType = GuessImageMediaType(entry.Name);
                if (mediaType is null)
                    continue;

                await using var stream = entry.Open();
                using var entryBuffer = new MemoryStream();
                await stream.CopyToAsync(entryBuffer, cancellationToken);

                var base64 = Convert.ToBase64String(entryBuffer.ToArray());
                result.Add(new SmoothDownloadedImage(
                    entry.Name,
                    mediaType,
                    $"data:{mediaType};base64,{base64}"));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private ResponseResult ToResponseResult(
        SmoothTaskResponse task,
        ResponseRequest request,
        StringBuilder providerEventLog,
        List<SmoothDownloadedImage> downloadedImages)
    {
        var text = ResolveFinalOutputText(task, providerEventLog);
        var createdAt = task.CreatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isCompleted = IsFinished(task.Status);

        var output = new List<object>
        {
            new
            {
                id = $"msg_{task.Id}",
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
        };

        for (var i = 0; i < downloadedImages.Count; i++)
        {
            var f = downloadedImages[i];
            output.Add(new
            {
                id = $"file_{i}_{task.Id}",
                type = "file_output",
                filename = f.FileName,
                media_type = f.MediaType,
                file_data = f.DataUrl
            });
        }

        return new ResponseResult
        {
            Id = task.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model ?? "smooth/smooth-agent",
            Temperature = request.Temperature,
            Metadata = MergeMetadata(request.Metadata, task, downloadedImages.Count),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                cost = task.CreditsUsed,
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Error = isCompleted
                ? null
                : new ResponseResultError
                {
                    Code = "smooth_task_failed",
                    Message = string.IsNullOrWhiteSpace(text)
                        ? "Smooth task failed before completion."
                        : text
                },
            Output = output
        };
    }

    private static Dictionary<string, object?> MergeMetadata(
        Dictionary<string, object?>? current,
        SmoothTaskResponse task,
        int downloadedImagesCount)
    {
        var merged = current is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(current);

        merged["smooth_task_id"] = task.Id;
        merged["smooth_status"] = task.Status;
        merged["smooth_live_url"] = task.LiveUrl;
        merged["smooth_recording_url"] = task.RecordingUrl;
        merged["smooth_downloads_url"] = task.DownloadsUrl;
        merged["smooth_downloaded_images_count"] = downloadedImagesCount;

        return merged;
    }

    private static string ResolveFinalOutputText(SmoothTaskResponse task, StringBuilder? providerEventLog)
    {
        var output = GetOutputText(task.Output);
        if (!string.IsNullOrWhiteSpace(output))
            return output!;

        if (providerEventLog is not null && providerEventLog.Length > 0)
            return providerEventLog.ToString().Trim();

        return string.Empty;
    }

    private static string? GetOutputText(JsonElement output)
    {
        if (output.ValueKind == JsonValueKind.String)
            return output.GetString();

        if (output.ValueKind == JsonValueKind.Object || output.ValueKind == JsonValueKind.Array)
            return output.GetRawText();

        return null;
    }

    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        if (request.Input?.IsText == true)
            return request.Input.Text ?? string.Empty;

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            var lines = new List<string>();
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var role = message.Role.ToString().ToLowerInvariant();
                var text = message.Content.IsText
                    ? message.Content.Text
                    : string.Join("\n", message.Content.Parts?
                        .OfType<InputTextPart>()
                        .Select(p => p.Text) ?? []);

                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add($"{role}: {text}");
            }

            if (lines.Count > 0)
                return string.Join("\n\n", lines);
        }

        return request.Instructions ?? string.Empty;
    }

    private static string BuildPromptFromCompletionMessages(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages ?? [])
        {
            var text = message.Content.GetRawText();
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
            var text = string.Join("\n", message.Parts
                .OfType<TextUIPart>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{message.Role}: {text}");
        }

        return string.Join("\n\n", lines);
    }

    private static string ExtractOutputText(IEnumerable<object> output)
    {
        foreach (var element in output.Select(o => JsonSerializer.SerializeToElement(o, SmoothJson)))
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

    private static IEnumerable<FileUIPart> ExtractFileUIPartsFromResponseOutput(IEnumerable<object> output)
    {
        foreach (var item in output)
        {
            var element = JsonSerializer.SerializeToElement(item, SmoothJson);
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            if (!element.TryGetProperty("type", out var typeEl)
                || !string.Equals(typeEl.GetString(), "file_output", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!element.TryGetProperty("file_data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
                continue;

            var dataUrl = dataEl.GetString();
            if (string.IsNullOrWhiteSpace(dataUrl))
                continue;

            var mediaType = element.TryGetProperty("media_type", out var mediaEl) && mediaEl.ValueKind == JsonValueKind.String
                ? mediaEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(mediaType) && dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var semicolon = dataUrl.IndexOf(';');
                if (semicolon > 5)
                    mediaType = dataUrl[5..semicolon];
            }

            if (string.IsNullOrWhiteSpace(mediaType))
                mediaType = "application/octet-stream";

            yield return new FileUIPart
            {
                MediaType = mediaType,
                Url = dataUrl
            };
        }
    }

    private static bool IsTerminal(string status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(string status)
        => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase);

    private static int ClampSteps(int? value)
        => Math.Clamp(value ?? 32, 2, 128);

    private static object? TryExtractStructuredOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema?.Schema is JsonElement element
            && element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return element;
        }

        return null;
    }

    private static string ResolveSmoothAgent(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "smooth";

        if (model.Contains("smooth-lite", StringComparison.OrdinalIgnoreCase))
            return "smooth-lite";

        return "smooth";
    }

    private static List<SmoothToolSignature>? ToSmoothToolSignatures(List<ResponseToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
            return null;

        var mapped = new List<SmoothToolSignature>();

        foreach (var tool in tools)
        {
            if (!string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase))
                continue;

            var extra = tool.Extra ?? new Dictionary<string, JsonElement>();
            if (!extra.TryGetValue("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                continue;

            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var description = extra.TryGetValue("description", out var descEl) && descEl.ValueKind == JsonValueKind.String
                ? descEl.GetString() ?? string.Empty
                : string.Empty;

            JsonElement inputs = default;
            if (extra.TryGetValue("parameters", out var paramEl) && paramEl.ValueKind == JsonValueKind.Object)
            {
                inputs = paramEl;
            }
            else if (extra.TryGetValue("input_schema", out var inputSchemaEl) && inputSchemaEl.ValueKind == JsonValueKind.Object)
            {
                inputs = inputSchemaEl;
            }
            else
            {
                inputs = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, SmoothJson);
            }

            mapped.Add(new SmoothToolSignature
            {
                Name = name,
                Description = description,
                Inputs = inputs,
                Output = "object"
            });
        }

        return mapped.Count == 0 ? null : mapped;
    }

    private static string? ExtractEventToolName(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        if (payload.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            return nameEl.GetString();

        if (payload.TryGetProperty("tool", out var toolEl) && toolEl.ValueKind == JsonValueKind.String)
            return toolEl.GetString();

        return null;
    }

    private static string? GuessImageMediaType(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private sealed class SmoothApiResponse<T>
    {
        [JsonPropertyName("r")]
        public T R { get; init; } = default!;
    }

    private sealed class SmoothSubmitTaskRequest
    {
        [JsonPropertyName("task")]
        public string Task { get; init; } = default!;

        [JsonPropertyName("agent")]
        public string Agent { get; init; } = "smooth";

        [JsonPropertyName("max_steps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxSteps { get; init; }

        [JsonPropertyName("response_model")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? ResponseModel { get; init; }

        [JsonPropertyName("custom_tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SmoothToolSignature>? CustomTools { get; init; }
    }

    private sealed class SmoothToolSignature
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = default!;

        [JsonPropertyName("description")]
        public string Description { get; init; } = string.Empty;

        [JsonPropertyName("inputs")]
        public JsonElement Inputs { get; init; }

        [JsonPropertyName("output")]
        public string Output { get; init; } = "object";
    }

    private sealed class SmoothTaskResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = default!;

        [JsonPropertyName("output")]
        public JsonElement Output { get; init; }

        [JsonPropertyName("credits_used")]
        public int? CreditsUsed { get; init; }

        [JsonPropertyName("live_url")]
        public string? LiveUrl { get; init; }

        [JsonPropertyName("recording_url")]
        public string? RecordingUrl { get; init; }

        [JsonPropertyName("downloads_url")]
        public string? DownloadsUrl { get; init; }

        [JsonPropertyName("created_at")]
        public long? CreatedAt { get; init; }

        [JsonPropertyName("events")]
        public List<SmoothTaskEvent>? Events { get; init; }
    }

    private sealed class SmoothTaskEvent
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = default!;

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; init; }

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; init; }
    }

    private sealed record SmoothDownloadedImage(string FileName, string MediaType, string DataUrl);
}

