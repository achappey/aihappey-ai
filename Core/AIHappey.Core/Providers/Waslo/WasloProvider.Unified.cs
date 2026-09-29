using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Waslo;

public sealed partial class WasloProvider
{
    private const int MaxMediaBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly HashSet<string> ReplyFields = ["message", "userId", "userName", "systemPrompt",
        "promptMode", "tools", "media", "responseFormat", "classify"];

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ValidateModel(request);
        var payload = BuildPayload(request);
        var raw = await SendReplyAsync(payload, request, cancellationToken);
        return MapReply(request.Model!, raw);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Waslo has no streaming API: generate protocol events only after the single reply completes.
        var result = await ExecuteUnifiedAsync(request, cancellationToken);
        var raw = (JsonElement)result.Metadata!["waslo.raw"]!;
        var id = GetString(raw, "request_id") ?? $"waslo-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var text = GetString(raw, "reply") ?? string.Empty;
        yield return Event("text-start", id, new AITextStartEventData(), now, result.Metadata);
        if (text.Length > 0)
            yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text }, now, result.Metadata);
        yield return Event("text-end", id, new AITextEndEventData(), now, result.Metadata);

        if (raw.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            foreach (var product in products.EnumerateArray())
                if (GetString(product, "imageUrl") is { } imageUrl && IsHttps(imageUrl))
                    yield return Event("file", id, new AIFileEventData { MediaType = "image/jpeg", Url = imageUrl }, now, result.Metadata);

        if (GetString(raw, "audioUrl") is { } audioUrl && IsHttps(audioUrl))
            yield return Event("file", id, new AIFileEventData { MediaType = "audio/mpeg", Url = audioUrl }, now, result.Metadata);

        yield return Event("finish", id, new AIFinishEventData
        {
            FinishReason = "stop", Model = request.Model, CompletedAt = now.ToUnixTimeSeconds(),
            Response = raw.Clone(),
            MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, now,
                additionalProperties: new Dictionary<string, object?> { [GetIdentifier()] = raw.Clone() })
        }, now, result.Metadata, result.Output);
    }

    private static void ValidateModel(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Model, "waslo/reply", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Waslo model 'waslo/reply'.", nameof(request));
    }

    private static JsonElement BuildPayload(AIRequest request)
    {
        var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.Metadata?.TryGetValue("waslo", out var options) == true && options is not null)
        {
            var scoped = options is JsonElement element ? element : JsonSerializer.SerializeToElement(options, Json);
            if (scoped.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Waslo provider metadata must be a JSON object.", nameof(request));
            foreach (var property in scoped.EnumerateObject())
            {
                if (!ReplyFields.Contains(property.Name))
                    throw new ArgumentException($"Unknown Waslo reply option '{property.Name}'.", nameof(request));
                body[property.Name] = property.Value.Clone();
            }
        }

        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = latest is null ? request.Input?.Text : string.Join("\n", latest.Content?.OfType<AITextContentPart>()
            .Select(part => part.Text).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
        if (!string.IsNullOrWhiteSpace(text)) body["message"] = JsonSerializer.SerializeToElement(text, Json);
        if (!body.TryGetValue("message", out var message) || message.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(message.GetString()) || message.GetString()!.Length > 4000)
            throw new ArgumentException("Waslo requires a message of 1–4000 characters.", nameof(request));

        if (!body.TryGetValue("userId", out var userId) || userId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(userId.GetString()))
            throw new ArgumentException("Waslo requires a stable userId in provider metadata or a configured end-user resolver for Vercel chat.", nameof(request));

        if (!body.ContainsKey("systemPrompt") && !string.IsNullOrWhiteSpace(request.Instructions))
            body["systemPrompt"] = JsonSerializer.SerializeToElement(request.Instructions, Json);

        foreach (var field in new[] { "userName", "systemPrompt" })
            if (body.TryGetValue(field, out var value) && value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"Waslo {field} must be a string.", nameof(request));
        if (body.TryGetValue("promptMode", out var mode) && (mode.ValueKind != JsonValueKind.String
            || mode.GetString() is not ("append" or "replace")))
            throw new ArgumentException("Waslo promptMode must be 'append' or 'replace'.", nameof(request));
        if (body.TryGetValue("responseFormat", out var format) && (format.ValueKind != JsonValueKind.String
            || format.GetString() is not ("text" or "voice")))
            throw new ArgumentException("Waslo responseFormat must be 'text' or 'voice'.", nameof(request));
        if (body.TryGetValue("classify", out var classify) && classify.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Waslo classify must be a boolean.", nameof(request));
        if (body.TryGetValue("tools", out var tools) && (tools.ValueKind != JsonValueKind.Array
            || tools.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String
                || item.GetString() is not ("catalog" or "calendar"))))
            throw new ArgumentException("Waslo tools may contain only 'catalog' and 'calendar'.", nameof(request));

        var files = latest?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count > 1 || files.Count > 0 && body.ContainsKey("media"))
            throw new ArgumentException("Waslo accepts only one media item, from a file part or provider metadata.", nameof(request));
        if (files.Count == 1) body["media"] = JsonSerializer.SerializeToElement(new[] { NormalizeFile(files[0]) }, Json);
        if (body.TryGetValue("media", out var media)) ValidateMedia(media);

        return JsonSerializer.SerializeToElement(body, Json);
    }

    private static object NormalizeFile(AIFileContentPart file)
    {
        var raw = file.Data switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => file.Data as string
        };
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("Waslo media file has no data.");
        var mime = file.MediaType;
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            if (comma < 0 || !raw[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Waslo requires a base64 data URL.");
            var embeddedMime = raw[5..(comma - 7)];
            if (!string.IsNullOrWhiteSpace(mime) && !string.Equals(mime, embeddedMime, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Waslo file MIME type differs from its data URL MIME type.");
            mime = embeddedMime;
            raw = raw[(comma + 1)..];
        }
        var type = MediaType(mime);
        ValidateBase64(raw);
        return new { type, base64 = raw, mimeType = mime };
    }

    private static string MediaType(string? mime) => mime?.ToLowerInvariant() switch
    {
        "application/pdf" => "pdf",
        _ when mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true => "image",
        _ when mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true => "audio",
        _ when mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true => "video",
        _ => throw new ArgumentException("Waslo supports image, audio, video and application/pdf media only.")
    };

    private static void ValidateMedia(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Array || media.GetArrayLength() != 1)
            throw new ArgumentException("Waslo media must contain exactly one item.");
        var item = media[0];
        if (item.ValueKind != JsonValueKind.Object || GetString(item, "mimeType") is not { } mime
            || GetString(item, "type") != MediaType(mime))
            throw new ArgumentException("Waslo media type and mimeType must match.");
        var base64 = GetString(item, "base64");
        if (base64 is null || item.TryGetProperty("url", out _))
            throw new ArgumentException("Waslo media requires base64 and mimeType (URL media is not supported by this adapter).");
        ValidateBase64(base64);
    }

    private static void ValidateBase64(string base64)
    {
        // Reject oversized data before allocating the decoded buffer.
        if (base64.Length > ((MaxMediaBytes + 2) / 3) * 4 + 4)
            throw new ArgumentException("Waslo media exceeds 10 MB.");
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0 || bytes.Length > MaxMediaBytes)
                throw new ArgumentException("Waslo media must be nonempty and at most 10 MB.");
        }
        catch (FormatException ex) { throw new ArgumentException("Waslo media contains invalid base64.", ex); }
    }

    private async Task<JsonElement> SendReplyAsync(JsonElement payload, AIRequest request, CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Waslo API key.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/agent/reply");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var idempotency = request.Headers?.FirstOrDefault(header =>
            string.Equals(header.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(idempotency))
        {
            if (idempotency.Length > 256 || idempotency.Any(c => c is '\r' or '\n' || char.IsControl(c)))
                throw new ArgumentException("Invalid Waslo Idempotency-Key header.", nameof(request));
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotency);
        }
        message.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null, detail = null, requestId = null;
            try
            {
                using var doc = JsonDocument.Parse(content);
                var error = doc.RootElement.TryGetProperty("error", out var value) ? value : default;
                code = GetString(error, "code"); detail = GetString(error, "message");
                requestId = GetString(error, "request_id");
            }
            catch (JsonException) { /* Do not echo arbitrary bodies or credentials. */ }
            var retry = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                && response.Headers.RetryAfter is { } after ? $" Retry-After: {after}." : string.Empty;
            throw new HttpRequestException($"Waslo reply failed ({(int)response.StatusCode}): {code ?? "upstream_error"}"
                + (detail is null ? "" : $" - {detail}")
                + (requestId is null ? "" : $" (request_id: {requestId})") + retry,
                null, response.StatusCode);
        }
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Waslo reply was not a JSON object.");
        return document.RootElement.Clone();
    }

    private AIResponse MapReply(string model, JsonElement raw)
    {
        var metadata = new Dictionary<string, object?> { ["waslo.raw"] = raw.Clone() };
        foreach (var key in new[] { "classification", "citations", "products", "audioUrl", "usage", "request_id" })
            if (raw.TryGetProperty(key, out var value)) metadata[$"waslo.{key}"] = value.Clone();
        var content = new List<AIContentPart>();
        if (GetString(raw, "reply") is { } text)
            content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = metadata });
        if (raw.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            foreach (var product in products.EnumerateArray())
                if (GetString(product, "imageUrl") is { } url && IsHttps(url))
                    content.Add(new AIFileContentPart { Type = "file", MediaType = "image/jpeg", Data = url,
                        Metadata = new() { ["waslo.product"] = product.Clone() } });
        if (GetString(raw, "audioUrl") is { } audioUrl && IsHttps(audioUrl))
            content.Add(new AIFileContentPart { Type = "file", MediaType = "audio/mpeg", Data = audioUrl, Metadata = metadata });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = model, Status = "completed",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata },
            Metadata = metadata
        };
    }

    private static string? GetString(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsHttps(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private AIStreamEvent Event(string type, string id, object data, DateTimeOffset now,
        Dictionary<string, object?>? metadata, AIOutput? output = null) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = now, Output = output }
    };
}
