using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    private static readonly JsonSerializerOptions BrowserUseJson = JsonSerializerOptions.Web;

    private sealed class BrowserUseNativeTerminalResult
    {
        public BrowserUseSessionResponse Created { get; init; } = default!;
        public BrowserUseSessionResponse Session { get; init; } = default!;
        public string OutputText { get; init; } = string.Empty;
        public string? DoneText { get; init; }
    }

    private abstract class BrowserUseNativeStreamEvent;

    private sealed class BrowserUseNativeArtifact
    {
        public string Kind { get; init; } = default!;
        public string Url { get; init; } = default!;
        public UIMessagePart Part { get; init; } = default!;
    }

    private sealed class BrowserUseNativeCreatedStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseSessionResponse Created { get; init; } = default!;
        public SourceUIPart? LiveSource { get; init; }
    }

    private sealed class BrowserUseNativeActionStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseNativeActionEvent Action { get; init; } = default!;
    }

    private sealed class BrowserUseNativeTerminalStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseNativeTerminalResult Terminal { get; init; } = default!;
        public IReadOnlyList<BrowserUseNativeArtifact> Artifacts { get; init; } = [];
    }

    private sealed class BrowserUseNativeArtifactStreamEvent : BrowserUseNativeStreamEvent
    {
        public BrowserUseNativeArtifact Artifact { get; init; } = default!;
    }

    private sealed class BrowserUseNativeActionEvent
    {
        public string SessionId { get; init; } = default!;
        public int StepNumber { get; init; }
        public int ActionIndex { get; init; }
        public string ToolCallId { get; init; } = default!;
        public string ToolName { get; init; } = default!;
        public object Input { get; init; } = default!;
        public object Output { get; init; } = default!;
        public bool IsDone { get; init; }
        public string? DoneText { get; init; }
    }

    private async Task<BrowserUseNativeTerminalResult> ExecuteNativeTaskAsync(BrowserUseCreateSessionRequest request, CancellationToken cancellationToken)
    {
        BrowserUseSessionResponse? created = null;

        try
        {
            created = await CreateSessionAsync(request, cancellationToken);
            var session = await WaitForSessionTerminalAsync(created.Id, cancellationToken);
            var doneText = TryExtractLatestDoneText(session);
            var outputText = ResolveFinalOutput(session, fallbackBuilder: null, doneText);

            return new BrowserUseNativeTerminalResult
            {
                Created = created,
                Session = session,
                DoneText = doneText,
                OutputText = outputText
            };
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created?.Id))
                await CleanupSessionAsync(created.Id, cancellationToken);
        }
    }

    private async IAsyncEnumerable<BrowserUseNativeStreamEvent> StreamNativeTaskAsync(
        BrowserUseCreateSessionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        BrowserUseSessionResponse? created = null;
        string? latestDoneText = null;
        string? latestScreenshotUrl = null;
        var emittedStepNumber = 0;

        try
        {
            created = await CreateSessionAsync(request, cancellationToken);

            yield return new BrowserUseNativeCreatedStreamEvent
            {
                Created = created,
                LiveSource = CreateLiveSourcePart(created)
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var session = await GetSessionAsync(created.Id, cancellationToken);

                if (!string.IsNullOrWhiteSpace(session.ScreenshotUrl)
                    && !string.Equals(session.ScreenshotUrl, latestScreenshotUrl, StringComparison.Ordinal))
                {
                    latestScreenshotUrl = session.ScreenshotUrl;

                    var screenshotPart = await DownloadSessionArtifactFilePartAsync(
                        session.ScreenshotUrl!,
                        session.Id,
                        "screenshot",
                        MediaTypeNames.Image.Jpeg,
                        cancellationToken);

                    if (screenshotPart is not null)
                    {
                        yield return new BrowserUseNativeArtifactStreamEvent
                        {
                            Artifact = new BrowserUseNativeArtifact
                            {
                                Kind = "screenshot",
                                Url = session.ScreenshotUrl!,
                                Part = screenshotPart
                            }
                        };
                    }
                }

                foreach (var actionEvent in ExtractActionEvents(session, emittedStepNumber))
                {
                    emittedStepNumber = Math.Max(emittedStepNumber, actionEvent.StepNumber);

                    if (!string.IsNullOrWhiteSpace(actionEvent.DoneText))
                        latestDoneText = actionEvent.DoneText;

                    yield return new BrowserUseNativeActionStreamEvent
                    {
                        Action = actionEvent
                    };
                }

                if (IsTerminal(session.Status))
                {
                    var outputText = ResolveFinalOutput(session, fallbackBuilder: null, latestDoneText);
                    var artifacts = new List<BrowserUseNativeArtifact>();

                    foreach (var recordingUrl in session.RecordingUrls
                                 .Where(url => !string.IsNullOrWhiteSpace(url))
                                 .Distinct(StringComparer.Ordinal))
                    {
                        var recordingPart = await DownloadSessionArtifactFilePartAsync(
                            recordingUrl,
                            session.Id,
                            "recording",
                            "video/mp4",
                            cancellationToken);

                        if (recordingPart is null)
                            continue;

                        artifacts.Add(new BrowserUseNativeArtifact
                        {
                            Kind = "recording",
                            Url = recordingUrl,
                            Part = recordingPart
                        });
                    }

                    yield return new BrowserUseNativeTerminalStreamEvent
                    {
                        Terminal = new BrowserUseNativeTerminalResult
                        {
                            Created = created,
                            Session = session,
                            DoneText = latestDoneText,
                            OutputText = outputText
                        },
                        Artifacts = artifacts
                    };

                    yield break;
                }

                await Task.Delay(800, cancellationToken);
            }

            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(created?.Id))
                await CleanupSessionAsync(created.Id, cancellationToken);
        }
    }

    private static IEnumerable<BrowserUseNativeActionEvent> ExtractActionEvents(BrowserUseSessionResponse session, int emittedStepNumber)
    {
        if (session.StepCount <= emittedStepNumber)
            yield break;

        for (var stepNumber = emittedStepNumber + 1; stepNumber <= session.StepCount; stepNumber++)
        {
            var summary = stepNumber == session.StepCount
                ? session.LastStepSummary
                : $"Browser step {stepNumber} completed.";

            yield return new BrowserUseNativeActionEvent
            {
                SessionId = session.Id,
                StepNumber = stepNumber,
                ActionIndex = 0,
                ToolCallId = $"bu_{session.Id}_s{stepNumber}_a1",
                ToolName = "browser_step",
                Input = new
                {
                    step = stepNumber,
                    summary,
                    liveUrl = session.LiveUrl,
                    status = session.Status
                },
                Output = new
                {
                    success = session.IsTaskSuccessful,
                    text = summary,
                    step = stepNumber,
                    liveUrl = session.LiveUrl,
                    status = session.Status,
                    action = "browser_step"
                },
                IsDone = false,
                DoneText = summary
            };
        }
    }

    private static string? TryExtractLatestDoneText(BrowserUseSessionResponse session)
    {
        if (session.Output.HasValue)
            return NormalizeTaskOutput(session.Output.Value);

        return string.IsNullOrWhiteSpace(session.LastStepSummary)
            ? null
            : session.LastStepSummary;
    }

    private static string ResolveFinalOutput(BrowserUseSessionResponse session, StringBuilder? fallbackBuilder, string? doneText)
    {
        if (session.Output.HasValue)
        {
            var normalized = NormalizeTaskOutput(session.Output.Value);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        if (!string.IsNullOrWhiteSpace(doneText))
            return doneText!;

        if (fallbackBuilder is not null && fallbackBuilder.Length > 0)
            return fallbackBuilder.ToString().Trim();

        return session.LastStepSummary ?? string.Empty;
    }

    private static string? NormalizeTaskOutput(JsonElement output)
    {
        if (output.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (output.ValueKind == JsonValueKind.String)
        {
            var text = output.GetString();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var trimmed = text.Trim();
            if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
                return text;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return NormalizeTaskOutput(doc.RootElement) ?? text;
            }
            catch
            {
                return text;
            }
        }

        if (output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("done", out var done))
            {
                var doneText = ExtractDoneText(done);
                if (!string.IsNullOrWhiteSpace(doneText))
                    return doneText;
            }

            var fallbackText = ExtractDoneText(output);
            if (!string.IsNullOrWhiteSpace(fallbackText))
                return fallbackText;

            return output.GetRawText();
        }

        if (output.ValueKind == JsonValueKind.Array)
            return output.GetRawText();

        return output.ToString();
    }

    private static string? ExtractDoneText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind != JsonValueKind.Object)
            return null;

        if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return text.GetString();

        if (value.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
            return output.GetString();

        if (value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
            return result.GetString();

        if (value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            return message.GetString();

        return null;
    }

    private SourceUIPart? CreateLiveSourcePart(BrowserUseSessionResponse session)
    {
        if (string.IsNullOrWhiteSpace(session.LiveUrl))
            return null;

        return new SourceUIPart
        {
            SourceId = $"browseruse-live-{session.Id}",
            Url = session.LiveUrl,
            Title = "Live browser session"
        };
    }

    private async Task<FileUIPart?> DownloadSessionArtifactFilePartAsync(
        string url,
        string sessionId,
        string artifactType,
        string? fallbackMime,
        CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var fileName = GetDownloadFileName(resp, url, artifactType, fallbackMime);
            var mediaType = resp.Content.Headers.ContentType?.MediaType
                            ?? GuessMimeTypeFromFileName(fileName)
                            ?? fallbackMime
                            ?? GuessMimeTypeFromUrl(url);

            return new FileUIPart
            {
                MediaType = string.Equals(mediaType, MediaTypeNames.Image.Jpeg, StringComparison.OrdinalIgnoreCase)
                    ? "image/jpg"
                    : mediaType,
                Url = Convert.ToBase64String(bytes)
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, Dictionary<string, object>?> CreateArtifactProviderMetadata(
        string sessionId,
        string artifactType,
        string url,
        string? fileName,
        string? mediaType)
        => new()
        {
            [nameof(BrowserUse).ToLowerInvariant()] = new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["artifactType"] = artifactType,
                ["url"] = url,
                ["filename"] = fileName,
                ["mediaType"] = mediaType
            }
            .Where(kvp => kvp.Value is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!)
        };

    private static string GetDownloadFileName(HttpResponseMessage response, string url, string artifactType, string? fallbackMime)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName.Trim('"');

        var parsedUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Split('?')[0];
        fileName = Path.GetFileName(parsedUrl);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        var extension = GuessFileExtension(fallbackMime)
                        ?? GuessFileExtension(GuessMimeTypeFromUrl(url))
                        ?? ".bin";

        return $"browseruse-{artifactType}-{Guid.NewGuid():n}{extension}";
    }

    private static string? GuessMimeTypeFromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => null
        };
    }

    private static string? GuessFileExtension(string? mimeType)
        => mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            _ => null
        };

    private static string GuessMimeTypeFromUrl(string url)
    {
        var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => MediaTypeNames.Application.Pdf,
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".csv" => "text/csv",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => MediaTypeNames.Application.Octet
        };
    }

    private async Task<BrowserUseSessionResponse> CreateSessionAsync(BrowserUseCreateSessionRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, BrowserUseJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v3/sessions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse create session failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseSessionResponse>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse create session returned empty payload.");
    }

    private async Task<BrowserUseSessionResponse> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/v3/sessions/{Uri.EscapeDataString(sessionId)}");
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse get session failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseSessionResponse>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse get session payload was empty.");
    }

    private async Task<BrowserUseSessionResponse> StopSessionAsync(string sessionId, string strategy, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new BrowserUseStopSessionRequest
        {
            Strategy = strategy
        }, BrowserUseJson);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v3/sessions/{Uri.EscapeDataString(sessionId)}/stop")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"BrowserUse stop session failed ({(int)resp.StatusCode}): {raw}");

        return JsonSerializer.Deserialize<BrowserUseSessionResponse>(raw, BrowserUseJson)
               ?? throw new InvalidOperationException("BrowserUse stop session payload was empty.");
    }

    private async Task<BrowserUseSessionResponse> WaitForSessionTerminalAsync(string sessionId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var session = await GetSessionAsync(sessionId, cancellationToken);
            if (IsTerminal(session.Status))
                return session;

            await Task.Delay(800, cancellationToken);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task CleanupSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var cleanupToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;

        if (cancellationToken.IsCancellationRequested)
            await StopSessionSafeAsync(sessionId, "task", cleanupToken);

        await DeleteSessionSafeAsync(sessionId, cleanupToken);
    }

    private async Task StopSessionSafeAsync(string sessionId, string strategy, CancellationToken cancellationToken)
    {
        try
        {
            await StopSessionAsync(sessionId, strategy, cancellationToken);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private async Task DeleteSessionSafeAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/v3/sessions/{Uri.EscapeDataString(sessionId)}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return;

            if (!resp.IsSuccessStatusCode)
            {
                var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"BrowserUse delete session failed ({(int)resp.StatusCode}): {raw}");
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static bool IsTerminal(string status)
        => string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "timed_out", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static bool IsFinished(string status)
        => string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase);

    private static long ToUnixTime(string? dateTimeUtc)
        => DateTimeOffset.TryParse(dateTimeUtc, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static long ParseUnixTimeOrNow(string? dateTimeUtc)
        => DateTimeOffset.TryParse(dateTimeUtc, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

    private static JsonElement? TryExtractStructuredOutputSchema(object? format)
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

    private static string ExtractOutputText(IEnumerable<object> output)
    {
        var first = output.FirstOrDefault();
        if (first is null)
            return string.Empty;

        var element = JsonSerializer.SerializeToElement(first, BrowserUseJson);
        if (!element.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static Dictionary<string, object> CreateGatewayCostMetadata(BrowserUseSessionResponse session)
    {
        var gateway = new Dictionary<string, object>();

        if (TryParseDecimal(session.TotalCostUsd, out var totalCost))
            gateway["cost"] = totalCost;

        if (TryParseDecimal(session.LlmCostUsd, out var llmCost))
            gateway["llmCost"] = llmCost;

        if (TryParseDecimal(session.ProxyCostUsd, out var proxyCost))
            gateway["proxyCost"] = proxyCost;

        if (TryParseDecimal(session.BrowserCostUsd, out var browserCost))
            gateway["browserCost"] = browserCost;

        if (TryParseDecimal(session.ProxyUsedMb, out var proxyUsedMb))
            gateway["proxyUsedMb"] = proxyUsedMb;

        return gateway;
    }

    private static bool TryParseDecimal(string? raw, out decimal value)
        => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private sealed class BrowserUseCreateSessionRequest
    {
        [JsonPropertyName("task")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Task { get; init; }

        [JsonPropertyName("model")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Model { get; init; }

        [JsonPropertyName("sessionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SessionId { get; init; }

        [JsonPropertyName("keepAlive")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool KeepAlive { get; init; }

        [JsonPropertyName("maxCostUsd")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MaxCostUsd { get; init; }

        [JsonPropertyName("profileId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProfileId { get; init; }

        [JsonPropertyName("workspaceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? WorkspaceId { get; init; }

        [JsonPropertyName("proxyCountryCode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProxyCountryCode { get; init; }

        [JsonPropertyName("outputSchema")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? OutputSchema { get; init; }

        [JsonPropertyName("enableScheduledTasks")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool EnableScheduledTasks { get; init; }

        [JsonPropertyName("enableRecording")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool EnableRecording { get; init; }

        [JsonPropertyName("skills")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Skills { get; init; }

        [JsonPropertyName("agentmail")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Agentmail { get; init; }

        [JsonPropertyName("cacheScript")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? CacheScript { get; init; }
    }

    private sealed class BrowserUseStopSessionRequest
    {
        [JsonPropertyName("strategy")]
        public string Strategy { get; init; } = "session";
    }

    private sealed class BrowserUseSessionResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; init; } = default!;

        [JsonPropertyName("model")]
        public string Model { get; init; } = default!;

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("output")]
        public JsonElement? Output { get; init; }

        [JsonPropertyName("outputSchema")]
        public JsonElement? OutputSchema { get; init; }

        [JsonPropertyName("stepCount")]
        public int StepCount { get; init; }

        [JsonPropertyName("lastStepSummary")]
        public string? LastStepSummary { get; init; }

        [JsonPropertyName("isTaskSuccessful")]
        public bool? IsTaskSuccessful { get; init; }

        [JsonPropertyName("liveUrl")]
        public string? LiveUrl { get; init; }

        [JsonPropertyName("recordingUrls")]
        public List<string> RecordingUrls { get; init; } = [];

        [JsonPropertyName("profileId")]
        public string? ProfileId { get; init; }

        [JsonPropertyName("workspaceId")]
        public string? WorkspaceId { get; init; }

        [JsonPropertyName("proxyCountryCode")]
        public string? ProxyCountryCode { get; init; }

        [JsonPropertyName("maxCostUsd")]
        public string? MaxCostUsd { get; init; }

        [JsonPropertyName("totalInputTokens")]
        public int TotalInputTokens { get; init; }

        [JsonPropertyName("totalOutputTokens")]
        public int TotalOutputTokens { get; init; }

        [JsonPropertyName("proxyUsedMb")]
        public string? ProxyUsedMb { get; init; }

        [JsonPropertyName("llmCostUsd")]
        public string? LlmCostUsd { get; init; }

        [JsonPropertyName("proxyCostUsd")]
        public string? ProxyCostUsd { get; init; }

        [JsonPropertyName("browserCostUsd")]
        public string? BrowserCostUsd { get; init; }

        [JsonPropertyName("totalCostUsd")]
        public string? TotalCostUsd { get; init; }

        [JsonPropertyName("screenshotUrl")]
        public string? ScreenshotUrl { get; init; }

        [JsonPropertyName("agentmailEmail")]
        public string? AgentmailEmail { get; init; }

        [JsonPropertyName("createdAt")]
        public string? CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public string? UpdatedAt { get; init; }
    }

    private sealed class BrowserUseRequestMetadata
    {
        public string? MaxCostUsd { get; init; }
        public string? ProfileId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? ProxyCountryCode { get; init; }
        public bool? EnableRecording { get; init; }
        public bool? Skills { get; init; }
        public bool? Agentmail { get; init; }
        public bool? CacheScript { get; init; }
    }
}
