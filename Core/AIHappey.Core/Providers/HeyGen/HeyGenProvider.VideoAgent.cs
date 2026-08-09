using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.HeyGen;

public partial class HeyGenProvider
{
    private const string VideoAgentChatModel = "video_agent/chat";
    private const string VideoAgentEndpoint = "v3/video-agents";
    private const string VideoAgentSessionToolName = "create_video_agent_session";
    private static readonly JsonSerializerOptions VideoAgentJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan DefaultVideoAgentPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultVideoAgentPollTimeout = TimeSpan.FromMinutes(10);

    private sealed record VideoAgentSessionResolution(string Id, bool Created, JsonElement? Raw, string? RunId);
    private sealed record VideoAgentTurn(VideoAgentSessionResolution Session, JsonElement Snapshot, IReadOnlyList<VideoAgentMessage> Messages, IReadOnlyList<VideoAgentFile> Files);
    private sealed record VideoAgentMessage(string Role, string Content, string Type, long? CreatedAt, IReadOnlyList<string> ResourceIds, JsonElement Raw);
    private sealed record VideoAgentFile(string ResourceId, string Filename, string MediaType, string Base64, JsonElement Raw);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = await ExecuteVideoAgentTurnAsync(request, cancellationToken);
        var output = new List<AIOutputItem>();

        if (turn.Session.Created && turn.Session.Raw is JsonElement rawSession)
            output.Add(CreateVideoAgentSessionToolItem(turn.Session.Id, rawSession));

        foreach (var message in turn.Messages.Where(message => string.Equals(message.Role, "model", StringComparison.OrdinalIgnoreCase)))
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = message.Content, Metadata = CreateVideoAgentPartMetadata(message.Raw) }]
            });
        }

        foreach (var file in turn.Files)
        {
            output.Add(new AIOutputItem
            {
                Type = "file",
                Content =
                [
                    new AIFileContentPart
                    {
                        Type = "file",
                        Filename = file.Filename,
                        MediaType = file.MediaType,
                        Data = file.Base64,
                        Metadata = CreateVideoAgentPartMetadata(file.Raw)
                    }
                ]
            });
        }

        var status = GetVideoAgentStatus(turn.Snapshot);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeVideoAgentModel(request.Model),
            Status = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "completed",
            Output = output.Count == 0 ? null : new AIOutput { Items = output },
            Metadata = CreateVideoAgentResponseMetadata(turn.Session, turn.Snapshot)
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = await ExecuteVideoAgentTurnAsync(request, cancellationToken);
        var timestamp = DateTimeOffset.UtcNow;
        var metadata = CreateVideoAgentResponseMetadata(turn.Session, turn.Snapshot);

        if (turn.Session.Created && turn.Session.Raw is JsonElement rawSession)
        {
            var toolCallId = BuildVideoAgentSessionToolCallId(turn.Session.Id);
            var providerMetadata = CreateVideoAgentToolProviderMetadata(turn.Session.Id);
            yield return CreateVideoAgentStreamEvent(toolCallId, "tool-input-available", new AIToolInputAvailableEventData
            {
                ToolName = VideoAgentSessionToolName,
                Title = "Create HeyGen Video Agent session",
                Input = new { prompt = ExtractLatestVideoAgentUserText(request), mode = "chat" },
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, metadata);
            yield return CreateVideoAgentStreamEvent(toolCallId, "tool-output-available", new AIToolOutputAvailableEventData
            {
                ToolName = VideoAgentSessionToolName,
                Output = CreateVideoAgentSessionToolOutput(turn.Session.Id, rawSession),
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, metadata);
        }

        var index = 0;
        foreach (var message in turn.Messages.Where(message => string.Equals(message.Role, "model", StringComparison.OrdinalIgnoreCase)))
        {
            var id = $"heygen-message-{turn.Session.Id}-{index++}";
            yield return CreateVideoAgentStreamEvent(id, "text-start", new AITextStartEventData(), timestamp, metadata);
            yield return CreateVideoAgentStreamEvent(id, "text-delta", new AITextDeltaEventData { Delta = message.Content }, timestamp, metadata);
            yield return CreateVideoAgentStreamEvent(id, "text-end", new AITextEndEventData(), timestamp, metadata);
        }

        foreach (var file in turn.Files)
        {
            yield return CreateVideoAgentStreamEvent($"heygen-resource-{file.ResourceId}", "file", new AIFileEventData
            {
                Filename = file.Filename,
                MediaType = file.MediaType,
                Url = $"data:{file.MediaType};base64,{file.Base64}",
                ProviderMetadata = CreateVideoAgentToolProviderMetadata(turn.Session.Id, file.ResourceId)
            }, timestamp, metadata);
        }

        var status = GetVideoAgentStatus(turn.Snapshot);
        yield return CreateVideoAgentStreamEvent(turn.Session.Id, "finish", new AIFinishEventData
        {
            FinishReason = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
            Model = NormalizeVideoAgentModel(request.Model),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(NormalizeVideoAgentModel(request.Model), DateTimeOffset.UtcNow)
        }, DateTimeOffset.UtcNow, metadata);
    }

    private async Task<VideoAgentTurn> ExecuteVideoAgentTurnAsync(AIRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();

        var prompt = ExtractLatestVideoAgentUserText(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("HeyGen Video Agent chat requires a user message.");

        var submittedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        VideoAgentSessionResolution session;
        if (TryFindVideoAgentSessionId(request, out var existingSessionId))
        {
            var sent = await SendVideoAgentMessageAsync(existingSessionId, request, prompt, cancellationToken);
            session = new VideoAgentSessionResolution(existingSessionId, false, null, ReadString(GetVideoAgentData(sent), "run_id"));
        }
        else
        {
            var created = await CreateVideoAgentSessionAsync(request, prompt, cancellationToken);
            var data = GetVideoAgentData(created);
            var sessionId = ReadString(data, "session_id") ?? ReadString(data, "sessionId")
                ?? throw new InvalidOperationException("HeyGen Video Agent create response did not include a session_id.");
            session = new VideoAgentSessionResolution(sessionId, true, data.Clone(), null);
        }

        var snapshot = await PollVideoAgentSessionAsync(session.Id, request, cancellationToken);
        var messages = ParseCurrentVideoAgentMessages(snapshot, submittedAt, prompt);
        var resourceIds = messages.SelectMany(message => message.ResourceIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var files = await DownloadVideoAgentResourcesAsync(session.Id, resourceIds, cancellationToken);
        return new VideoAgentTurn(session, snapshot, messages, files);
    }

    private async Task<JsonElement> CreateVideoAgentSessionAsync(AIRequest request, string prompt, CancellationToken cancellationToken)
    {
        var body = BuildVideoAgentPayload(request, prompt, create: true);
        body["mode"] = "chat";
        body["prompt"] = prompt.Trim();
        return await SendVideoAgentJsonAsync(HttpMethod.Post, VideoAgentEndpoint, body, "create session", cancellationToken);
    }

    private async Task<JsonElement> SendVideoAgentMessageAsync(string sessionId, AIRequest request, string prompt, CancellationToken cancellationToken)
    {
        var body = BuildVideoAgentPayload(request, prompt, create: false);
        body["message"] = prompt.Trim();
        return await SendVideoAgentJsonAsync(HttpMethod.Post, $"{VideoAgentEndpoint}/{Uri.EscapeDataString(sessionId)}", body, "send message", cancellationToken);
    }

    private Dictionary<string, object?> BuildVideoAgentPayload(AIRequest request, string prompt, bool create)
    {
        var body = new Dictionary<string, object?>();
        var allowed = create
            ? new[] { "avatar_id", "voice_id", "style_id", "brand_kit_id", "orientation", "callback_url", "callback_id", "incognito_mode" }
            : new[] { "avatar_id", "voice_id", "brand_kit_id" };

        foreach (var name in allowed)
        {
            var value = GetVideoAgentProviderOption(request, name);
            if (value is not null)
                body[name] = value;
        }

        var files = BuildVideoAgentInputFiles(request).ToList();
        if (files.Count > 0)
            body["files"] = files;

        if (create)
        {
            var styleId = ParseStyleIdFromVideoModel(request.Model ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(styleId) && !body.ContainsKey("style_id"))
                body["style_id"] = styleId;
            body["prompt"] = prompt;
        }
        else
        {
            body["message"] = prompt;
        }

        return body;
    }

    private object? GetVideoAgentProviderOption(AIRequest request, string name)
    {
        try
        {
            return request.Metadata?.GetProviderOption<object>(GetIdentifier(), name);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object> BuildVideoAgentInputFiles(AIRequest request)
    {
        foreach (var file in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIFileContentPart>() ?? [])
        {
            switch (file.Data)
            {
                case byte[] bytes:
                    yield return new { type = "base64", media_type = file.MediaType ?? MediaTypeNames.Application.Octet, data = Convert.ToBase64String(bytes) };
                    break;
                case string value when Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https":
                    yield return new { type = "url", url = value };
                    break;
                case string value when TryParseDataUri(value, out var mediaType, out var base64):
                    yield return new { type = "base64", media_type = mediaType, data = base64 };
                    break;
                case string value when !string.IsNullOrWhiteSpace(value):
                    yield return new { type = "base64", media_type = file.MediaType ?? MediaTypeNames.Application.Octet, data = value };
                    break;
            }
        }
    }

    private async Task<JsonElement> PollVideoAgentSessionAsync(string sessionId, AIRequest request, CancellationToken cancellationToken)
    {
        var intervalMs = TryConvertInt(GetVideoAgentProviderOption(request, "poll_interval_ms")) ?? (int)DefaultVideoAgentPollInterval.TotalMilliseconds;
        var timeoutMs = TryConvertInt(GetVideoAgentProviderOption(request, "poll_timeout_ms")) ?? (int)DefaultVideoAgentPollTimeout.TotalMilliseconds;
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            var root = await SendVideoAgentJsonAsync(HttpMethod.Get, $"{VideoAgentEndpoint}/{Uri.EscapeDataString(sessionId)}", null, "get session", cancellationToken);
            var data = GetVideoAgentData(root).Clone();
            if (IsVideoAgentTerminal(GetVideoAgentStatus(data)))
                return data;

            if (DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(Math.Max(1, timeoutMs)))
                throw new TimeoutException($"Timed out waiting for HeyGen Video Agent session '{sessionId}'.");

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, intervalMs)), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<VideoAgentFile>> DownloadVideoAgentResourcesAsync(string sessionId, IEnumerable<string> resourceIds, CancellationToken cancellationToken)
    {
        var files = new List<VideoAgentFile>();
        foreach (var resourceId in resourceIds)
        {
            var root = await SendVideoAgentJsonAsync(HttpMethod.Get,
                $"{VideoAgentEndpoint}/{Uri.EscapeDataString(sessionId)}/resources/{Uri.EscapeDataString(resourceId)}",
                null, "get resource", cancellationToken);
            var resource = GetVideoAgentData(root);
            var url = ReadString(resource, "url") ?? ReadString(resource, "preview_url") ?? ReadString(resource, "thumbnail_url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var response = await _client.GetAsync(url, cancellationToken);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HeyGen resource download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? GuessResourceMediaType(url, ReadString(resource, "resource_type"));
            files.Add(new VideoAgentFile(resourceId, GetResourceFilename(resourceId, url, mediaType), mediaType, Convert.ToBase64String(bytes), resource.Clone()));
        }

        return files;
    }

    private async Task<JsonElement> SendVideoAgentJsonAsync(HttpMethod method, string uri, object? payload, string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, VideoAgentJson), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HeyGen Video Agent {operation} failed ({(int)response.StatusCode}): {raw}");
        if (string.IsNullOrWhiteSpace(raw))
            return JsonSerializer.SerializeToElement(new { }, VideoAgentJson);

        using var document = JsonDocument.Parse(raw);
        EnsureNoHeyGenVideoApiError(document.RootElement, raw);
        return document.RootElement.Clone();
    }

    private bool TryFindVideoAgentSessionId(AIRequest request, out string sessionId)
    {
        sessionId = GetVideoAgentStringOption(request, "sessionId") ?? GetVideoAgentStringOption(request, "session_id") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var tool in request.Input?.Items?.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>() ?? [])
        {
            if (tool.ProviderExecuted != true || !string.Equals(tool.ToolName, VideoAgentSessionToolName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryExtractVideoAgentSessionId(tool.Output, out sessionId) || TryExtractVideoAgentSessionId(tool.Metadata, out sessionId))
                return true;
        }

        sessionId = string.Empty;
        return false;
    }

    private string? GetVideoAgentStringOption(AIRequest request, string name)
    {
        try { return request.Metadata?.GetProviderOption<string>(GetIdentifier(), name); }
        catch { return null; }
    }

    private static bool TryExtractVideoAgentSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;
        var element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, VideoAgentJson);
        return TryExtractVideoAgentSessionId(element, out sessionId);
    }

    private static bool TryExtractVideoAgentSessionId(JsonElement element, out string sessionId)
    {
        sessionId = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if ((string.Equals(property.Name, "sessionId", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "session_id", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                sessionId = property.Value.GetString()!;
                return true;
            }
            if (property.Value.ValueKind == JsonValueKind.Object && TryExtractVideoAgentSessionId(property.Value, out sessionId))
                return true;
        }
        return false;
    }

    private static IReadOnlyList<VideoAgentMessage> ParseCurrentVideoAgentMessages(JsonElement snapshot, long submittedAt, string prompt)
    {
        if (!TryGetPropertyIgnoreCase(snapshot, "messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            return [];

        var parsed = messages.EnumerateArray().Select(ParseVideoAgentMessage).Where(message => message is not null).Cast<VideoAgentMessage>().ToList();
        var current = parsed.Where(message => message.CreatedAt is null || message.CreatedAt >= submittedAt - 1).ToList();

        if (current.Count == 0)
        {
            var marker = parsed.FindIndex(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Content.Trim(), prompt.Trim(), StringComparison.Ordinal));
            if (marker >= 0)
                current = parsed.Take(marker).ToList();
        }

        return current.OrderBy(message => message.CreatedAt ?? long.MaxValue).ToList();
    }

    private static VideoAgentMessage? ParseVideoAgentMessage(JsonElement message)
    {
        var role = ReadString(message, "role");
        var content = ReadString(message, "content");
        if (string.IsNullOrWhiteSpace(role) || content is null)
            return null;
        var resourceIds = TryGetPropertyIgnoreCase(message, "resource_ids", out var resources) && resources.ValueKind == JsonValueKind.Array
            ? resources.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()
            : [];
        return new VideoAgentMessage(role, content, ReadString(message, "type") ?? "text", ReadInt64(message, "created_at"), resourceIds, message.Clone());
    }

    private static string ExtractLatestVideoAgentUserText(AIRequest request)
        => request.Input?.Items?.AsEnumerable().Reverse()
               .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
               .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)) ?? []))
               .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
           ?? request.Input?.Text
           ?? request.Instructions
           ?? string.Empty;

    private AIOutputItem CreateVideoAgentSessionToolItem(string sessionId, JsonElement rawSession)
        => new()
        {
            Type = "tool-call",
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = BuildVideoAgentSessionToolCallId(sessionId),
                    ToolName = VideoAgentSessionToolName,
                    Title = "Create HeyGen Video Agent session",
                    Input = new { mode = "chat" },
                    Output = CreateVideoAgentSessionToolOutput(sessionId, rawSession),
                    ProviderExecuted = true,
                    State = "output-available",
                    Metadata = new Dictionary<string, object?> { [GetIdentifier()] = CreateVideoAgentSessionToolOutput(sessionId, rawSession) }
                }
            ]
        };

    private static object CreateVideoAgentSessionToolOutput(string sessionId, JsonElement rawSession)
        => new { structuredContent = new { type = VideoAgentSessionToolName, sessionId, session_id = sessionId, session = rawSession.Clone() } };

    private Dictionary<string, object?> CreateVideoAgentResponseMetadata(VideoAgentSessionResolution session, JsonElement snapshot)
        => new()
        {
            ["heygen.sessionId"] = session.Id,
            ["heygen.session_id"] = session.Id,
            ["heygen.run_id"] = session.RunId,
            ["heygen.video_id"] = ReadString(snapshot, "video_id"),
            ["heygen.status"] = GetVideoAgentStatus(snapshot),
            ["heygen.progress"] = ReadInt64(snapshot, "progress"),
            ["heygen.session"] = snapshot.Clone()
        };

    private static Dictionary<string, object?> CreateVideoAgentPartMetadata(JsonElement raw)
        => new() { ["heygen.raw"] = raw.Clone() };

    private static Dictionary<string, Dictionary<string, object>> CreateVideoAgentToolProviderMetadata(string sessionId, string? resourceId = null)
        => new()
        {
            [ProviderId] = new Dictionary<string, object>
            {
                ["type"] = resourceId is null ? VideoAgentSessionToolName : "video_agent_resource",
                ["tool_name"] = VideoAgentSessionToolName,
                ["sessionId"] = sessionId,
                ["session_id"] = sessionId,
                ["resource_id"] = resourceId ?? string.Empty
            }
        };

    private AIStreamEvent CreateVideoAgentStreamEvent(string id, string type, object data, DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new() { ProviderId = GetIdentifier(), Metadata = metadata, Event = new AIEventEnvelope { Id = id, Type = type, Data = data, Timestamp = timestamp } };

    private static JsonElement GetVideoAgentData(JsonElement root)
        => TryGetPropertyIgnoreCase(root, "data", out var data) && data.ValueKind == JsonValueKind.Object ? data : root;

    private static string GetVideoAgentStatus(JsonElement snapshot) => ReadString(snapshot, "status")?.Trim().ToLowerInvariant() ?? "unknown";
    private static bool IsVideoAgentTerminal(string status) => status is "waiting_for_input" or "completed" or "failed";
    private string NormalizeVideoAgentModel(string? model) => string.IsNullOrWhiteSpace(model)
        ? VideoAgentChatModel.ToModelId(GetIdentifier()) : model.ToModelId(GetIdentifier());
    private static string BuildVideoAgentSessionToolCallId(string sessionId) => $"heygen-create-session-{sessionId}";

    private static long? ReadInt64(JsonElement value, string name)
        => TryGetPropertyIgnoreCase(value, name, out var property) && property.TryGetInt64(out var result) ? result : null;

    private static int? TryConvertInt(object? value)
        => value is null ? null : int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var result) ? result : null;

    private static bool TryParseDataUri(string value, out string mediaType, out string base64)
    {
        mediaType = string.Empty;
        base64 = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var separator = value.IndexOf(',');
        if (separator < 0 || !value[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
            return false;
        mediaType = value[5..value[..separator].LastIndexOf(';')];
        base64 = value[(separator + 1)..];
        return !string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(base64);
    }

    private static string GuessResourceMediaType(string url, string? resourceType)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(resourceType, "video", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        return MediaTypeNames.Application.Octet;
    }

    private static string GetResourceFilename(string resourceId, string url, string mediaType)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var filename = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(filename)) return filename;
        }
        var extension = mediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "video/webm" => ".webm", "video/quicktime" => ".mov", "video/mp4" => ".mp4", _ => ".bin" };
        return resourceId + extension;
    }
}
