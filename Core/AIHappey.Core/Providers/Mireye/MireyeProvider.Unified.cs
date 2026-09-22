using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Mireye;

public partial class MireyeProvider
{
    private static readonly JsonSerializerOptions MireyeJson = JsonSerializerOptions.Web;

    private sealed record MireyeRequestPayload(
        string Question,
        string? Address,
        double? Lat,
        double? Lng,
        bool IncludeTrace);

    private sealed record MireyeSseFrame(string Event, string Data);

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = BuildMireyeRequestPayload(request);
        var final = await SendMireyeAskAsync(payload, cancellationToken);
        return CreateMireyeResponse(final);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = BuildMireyeRequestPayload(request);
        var requestBody = ToMireyeApiPayload(payload);
        var textId = request.Id ?? $"mireye-text-{Guid.NewGuid():N}";
        var textStarted = false;
        var textEnded = false;
        var finalReceived = false;

        using var httpRequest = CreateMireyeRequest(HttpMethod.Post, "v1/ask/stream");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(requestBody, MireyeJson),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureMireyeSuccessAsync(response, "stream ask", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        await foreach (var frame in ReadMireyeSseAsync(reader, cancellationToken))
        {
            JsonElement data;
            try
            {
                data = JsonSerializer.Deserialize<JsonElement>(frame.Data, MireyeJson).Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Mireye stream event '{frame.Event}' contained invalid JSON.", exception);
            }

            var timestamp = DateTimeOffset.UtcNow;
            switch (frame.Event)
            {
                case "delta":
                    var delta = GetMireyeString(data, "text") ?? string.Empty;
                    if (!textStarted)
                    {
                        textStarted = true;
                        yield return CreateMireyeEvent(
                            "text-start",
                            textId,
                            new AITextStartEventData(),
                            timestamp,
                            CreateMireyeRawMetadata("delta", data));
                    }

                    if (delta.Length > 0)
                    {
                        yield return CreateMireyeEvent(
                            "text-delta",
                            textId,
                            new AITextDeltaEventData
                            {
                                Delta = delta,
                                ProviderMetadata = CreateMireyeTextProviderMetadata("delta", data)
                            },
                            timestamp,
                            CreateMireyeRawMetadata("delta", data));
                    }
                    break;

                case "final":
                    finalReceived = true;
                    var finalMetadata = CreateMireyeResponseMetadata(data);
                    var finalAnswer = GetMireyeString(data, "answer") ?? string.Empty;
                    if (!textStarted && finalAnswer.Length > 0)
                    {
                        textStarted = true;
                        yield return CreateMireyeEvent(
                            "text-start",
                            textId,
                            new AITextStartEventData(),
                            timestamp,
                            finalMetadata);
                        yield return CreateMireyeEvent(
                            "text-delta",
                            textId,
                            new AITextDeltaEventData { Delta = finalAnswer },
                            timestamp,
                            finalMetadata);
                    }

                    if (textStarted && !textEnded)
                    {
                        textEnded = true;
                        yield return CreateMireyeEvent(
                            "text-end",
                            textId,
                            new AITextEndEventData(),
                            timestamp,
                            finalMetadata);
                    }

                    foreach (var sourceEvent in CreateMireyeSourceEvents(data, timestamp, finalMetadata))
                        yield return sourceEvent;

                    yield return CreateMireyeEvent(
                        "data-mireye.final",
                        textId,
                        new AIDataEventData { Id = textId, Data = data.Clone(), Transient = false },
                        timestamp,
                        finalMetadata);

                    yield return CreateMireyeEvent(
                        "finish",
                        textId,
                        new AIFinishEventData
                        {
                            FinishReason = "stop",
                            Model = MireyeAskModel.ToModelId(GetIdentifier()),
                            CompletedAt = timestamp.ToUnixTimeSeconds(),
                            MessageMetadata = AIFinishMessageMetadata.Create(
                                MireyeAskModel.ToModelId(GetIdentifier()),
                                timestamp,
                                additionalProperties: new Dictionary<string, object?>
                                {
                                    [GetIdentifier()] = data.Clone()
                                })
                        },
                        timestamp,
                        finalMetadata);
                    yield break;

                case "error":
                    if (textStarted && !textEnded)
                    {
                        textEnded = true;
                        yield return CreateMireyeEvent(
                            "text-end",
                            textId,
                            new AITextEndEventData(),
                            timestamp,
                            CreateMireyeRawMetadata("error", data));
                    }

                    yield return CreateMireyeEvent(
                        "error",
                        textId,
                        new AIErrorEventData { ErrorText = ExtractMireyeError(data) },
                        timestamp,
                        CreateMireyeRawMetadata("error", data));
                    yield break;
            }
        }

        if (!finalReceived)
        {
            if (textStarted && !textEnded)
            {
                yield return CreateMireyeEvent(
                    "text-end",
                    textId,
                    new AITextEndEventData(),
                    DateTimeOffset.UtcNow,
                    null);
            }

            yield return CreateMireyeEvent(
                "error",
                textId,
                new AIErrorEventData
                {
                    ErrorText = "Mireye stream ended without its authoritative final frame; discard any partial answer."
                },
                DateTimeOffset.UtcNow,
                null);
        }
    }

    private async Task<JsonElement> SendMireyeAskAsync(
        MireyeRequestPayload payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateMireyeRequest(HttpMethod.Post, "v1/ask");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        request.Content = new StringContent(
            JsonSerializer.Serialize(ToMireyeApiPayload(payload), MireyeJson),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureMireyeSuccessAsync(response, "ask", cancellationToken);
        return await ReadMireyeJsonAsync(response, "ask", cancellationToken);
    }

    private HttpRequestMessage CreateMireyeRequest(HttpMethod method, string path)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Mireye)} API key.");

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static async Task EnsureMireyeSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(CultureInfo.InvariantCulture)
                         ?? response.Headers.RetryAfter?.Date?.ToString("O", CultureInfo.InvariantCulture);
        var suffix = string.IsNullOrWhiteSpace(retryAfter) ? string.Empty : $" Retry-After: {retryAfter}.";
        throw new HttpRequestException(
            $"Mireye {operation} failed with status {(int)response.StatusCode} ({response.StatusCode}).{suffix} {body}",
            null,
            response.StatusCode);
    }

    private static async Task<JsonElement> ReadMireyeJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body, MireyeJson).Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Mireye {operation} returned invalid JSON.", exception);
        }
    }

    private MireyeRequestPayload BuildMireyeRequestPayload(AIRequest request)
    {
        NormalizeMireyeModel(request.Model);
        var question = ExtractLatestMireyeUserText(request);
        var metadata = GetMireyeMetadata(request);

        var address = GetMireyeMetadataString(metadata, "address")?.Trim();
        var hasAddress = !string.IsNullOrWhiteSpace(address);
        var lat = GetMireyeMetadataDouble(metadata, "lat");
        var lng = GetMireyeMetadataDouble(metadata, "lng");
        var hasAnyCoordinate = lat.HasValue || lng.HasValue;

        if (hasAddress && hasAnyCoordinate)
            throw new ArgumentException("Mireye provider metadata must contain either 'address' or 'lat' and 'lng', never both.", nameof(request));
        if (!hasAddress && !hasAnyCoordinate)
            throw new ArgumentException("Mireye requires provider metadata containing either 'address' or both 'lat' and 'lng'.", nameof(request));
        if (!hasAddress && (!lat.HasValue || !lng.HasValue))
            throw new ArgumentException("Mireye coordinate metadata requires both exact keys 'lat' and 'lng'.", nameof(request));
        if (lat.HasValue && (!double.IsFinite(lat.Value) || lat.Value is < -90 or > 90))
            throw new ArgumentOutOfRangeException(nameof(request), "Mireye 'lat' must be finite and between -90 and 90.");
        if (lng.HasValue && (!double.IsFinite(lng.Value) || lng.Value is < -180 or > 180))
            throw new ArgumentOutOfRangeException(nameof(request), "Mireye 'lng' must be finite and between -180 and 180.");

        return new MireyeRequestPayload(
            question,
            hasAddress ? address : null,
            lat,
            lng,
            GetMireyeMetadataBoolean(metadata, "include_trace") ?? false);
    }

    private static Dictionary<string, object?> ToMireyeApiPayload(MireyeRequestPayload payload)
    {
        var result = new Dictionary<string, object?>
        {
            ["question"] = payload.Question,
            ["include_trace"] = payload.IncludeTrace
        };
        if (payload.Address is not null)
            result["address"] = payload.Address;
        else
        {
            result["lat"] = payload.Lat;
            result["lng"] = payload.Lng;
        }
        return result;
    }

    private static string NormalizeMireyeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Mireye requires model 'mireye/ask'.", nameof(model));
        var normalized = model.Trim().Trim('/');
        if (normalized.StartsWith("mireye/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["mireye/".Length..];
        if (!string.Equals(normalized, MireyeAskModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Mireye model '{model}' is not supported. Use 'mireye/ask'.");
        return MireyeAskModel;
    }

    private static string ExtractLatestMireyeUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = string.Join(
                "\n",
                item.Content?.OfType<AITextContentPart>()
                    .Select(part => part.Text)
                    .Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            return request.Instructions;
        throw new InvalidOperationException("Mireye requires a non-empty user question.");
    }

    private static JsonElement GetMireyeMetadata(AIRequest request)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue("mireye", out var raw)
            || raw is null)
        {
            return JsonSerializer.SerializeToElement(new { }, MireyeJson);
        }

        var metadata = raw is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(raw, MireyeJson);
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Mireye provider metadata must be a JSON object.", nameof(request));
        return metadata.Clone();
    }

    private static string? GetMireyeMetadataString(JsonElement metadata, string name)
        => metadata.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetMireyeMetadataDouble(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return number;
        throw new ArgumentException($"Mireye provider metadata '{name}' must be numeric.");
    }

    private static bool? GetMireyeMetadataBoolean(JsonElement metadata, string name)
    {
        if (!metadata.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"Mireye provider metadata '{name}' must be boolean.")
        };
    }

    private AIResponse CreateMireyeResponse(JsonElement final)
    {
        var answer = GetMireyeString(final, "answer") ?? string.Empty;
        var content = new List<AIContentPart>();
        if (answer.Length > 0)
        {
            content.Add(new AITextContentPart
            {
                Type = "text",
                Text = answer,
                Metadata = new Dictionary<string, object?>
                {
                    ["mireye.confidence"] = GetMireyeString(final, "confidence"),
                    ["mireye.fields_used"] = GetMireyeProperty(final, "fields_used")
                }
            });
        }

        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content = content,
                Metadata = CreateMireyeResponseMetadata(final)
            }
        };
        items.AddRange(CreateMireyeSourceOutputItems(final));

        var metadata = CreateMireyeResponseMetadata(final);
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = MireyeAskModel.ToModelId(GetIdentifier()),
            Status = "completed",
            Output = new AIOutput { Items = items, Metadata = metadata },
            Metadata = metadata
        };
    }

    private static IEnumerable<AIOutputItem> CreateMireyeSourceOutputItems(JsonElement final)
    {
        foreach (var citation in EnumerateMireyeCitations(final))
        {
            var url = GetMireyeString(citation, "source_url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var title = GetMireyeString(citation, "source") ?? url;
            yield return new AIOutputItem
            {
                Type = "source-url",
                Content = [new AITextContentPart { Type = "text", Text = title }],
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.source.url"] = url,
                    ["chatcompletions.source.title"] = title,
                    ["messages.source.url"] = url,
                    ["messages.source.title"] = title,
                    ["mireye.citation"] = citation.Clone()
                }
            };
        }
    }

    private IEnumerable<AIStreamEvent> CreateMireyeSourceEvents(
        JsonElement final,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        var index = 0;
        foreach (var citation in EnumerateMireyeCitations(final))
        {
            var url = GetMireyeString(citation, "source_url");
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var title = GetMireyeString(citation, "source") ?? url;
            yield return CreateMireyeEvent(
                "source-url",
                $"mireye-source-{index++}",
                new AISourceUrlEventData
                {
                    SourceId = url,
                    Url = url,
                    Title = title,
                    Type = "url_citation",
                    ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                    {
                        [GetIdentifier()] = new Dictionary<string, object>
                        {
                            ["citation"] = citation.Clone()
                        }
                    }
                },
                timestamp,
                metadata);
        }
    }

    private static IEnumerable<JsonElement> EnumerateMireyeCitations(JsonElement final)
    {
        if (TryGetMireyeProperty(final, "citations", out var citations)
            && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in citations.EnumerateArray())
                if (citation.ValueKind == JsonValueKind.Object)
                    yield return citation.Clone();
        }
    }

    private static Dictionary<string, object?> CreateMireyeResponseMetadata(JsonElement final)
        => new()
        {
            ["mireye.answer"] = GetMireyeString(final, "answer"),
            ["mireye.confidence"] = GetMireyeString(final, "confidence"),
            ["mireye.answered_at"] = GetMireyeString(final, "answered_at"),
            ["mireye.fields_used"] = GetMireyeProperty(final, "fields_used"),
            ["mireye.citations"] = GetMireyeProperty(final, "citations"),
            ["mireye.data_gaps"] = GetMireyeProperty(final, "data_gaps"),
            ["mireye.resolved_location"] = GetMireyeProperty(final, "resolved_location"),
            ["mireye.geocode"] = GetMireyeProperty(final, "geocode"),
            ["mireye.trace"] = GetMireyeProperty(final, "trace"),
            ["mireye.raw"] = final.Clone()
        };

    private static Dictionary<string, object?> CreateMireyeRawMetadata(string eventName, JsonElement raw)
        => new()
        {
            ["mireye.event"] = eventName,
            ["mireye.raw"] = raw.Clone()
        };

    private static Dictionary<string, object> CreateMireyeTextProviderMetadata(string eventName, JsonElement raw)
        => new()
        {
            ["mireye"] = new Dictionary<string, object>
            {
                ["event"] = eventName,
                ["raw"] = raw.Clone()
            }
        };

    private AIStreamEvent CreateMireyeEvent(
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static async IAsyncEnumerable<MireyeSseFrame> ReadMireyeSseAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(eventName) && data.Length > 0)
                    yield return new MireyeSseFrame(eventName, data.ToString());
                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
                continue;
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                    data.AppendLine();
                data.Append(line[5..].TrimStart());
            }
        }

        if (!string.IsNullOrWhiteSpace(eventName) && data.Length > 0)
            yield return new MireyeSseFrame(eventName, data.ToString());
    }

    private static string ExtractMireyeError(JsonElement data)
        => GetMireyeString(data, "message")
           ?? GetMireyeString(data, "error")
           ?? "Mireye stream failed.";

    private static object? GetMireyeProperty(JsonElement element, string name)
        => TryGetMireyeProperty(element, name, out var value) ? value.Clone() : null;

    private static string? GetMireyeString(JsonElement element, string name)
        => TryGetMireyeProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetMireyeProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }
}
