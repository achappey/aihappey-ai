using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.XiaomiMIMO;

public partial class XiaomiMIMOProvider
{
    private const string XiaomiMimoAsrModel = "mimo-v2.5-asr";

    private static readonly JsonSerializerOptions XiaomiMimoTranscriptionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = PrepareXiaomiMimoTranscription(request, streaming: false);
        var requestBody = JsonSerializer.Serialize(prepared.Payload, XiaomiMimoTranscriptionJsonOptions);

        ApplyAuthHeader();
        using var httpRequest = CreateXiaomiMimoAudioRequest(requestBody, streaming: false);
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xiaomi MiMo transcription request failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        var text = ExtractXiaomiMimoChoiceText(root, "message");
        if (text is null)
            throw new InvalidOperationException($"Xiaomi MiMo transcription response did not include choices[].message.content. Body: {body}");

        return new TranscriptionResponse
        {
            Text = text,
            Language = prepared.Language == "auto" ? null : prepared.Language,
            DurationInSeconds = TryGetXiaomiMimoSingle(root, "usage", "seconds"),
            Segments = [],
            Warnings = prepared.Warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = request.Model,
                    asr_options = new { language = prepared.Language },
                    response = root
                }, JsonSerializerOptions.Web)
            },
            Request = new TranscriptionRequestItem { Body = requestBody },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var prepared = PrepareXiaomiMimoTranscription(request, streaming: true);
        var requestBody = JsonSerializer.Serialize(prepared.Payload, XiaomiMimoTranscriptionJsonOptions);
        var transcript = new StringBuilder();

        await foreach (var root in SendXiaomiMimoAudioStreamAsync(requestBody, "transcription", cancellationToken))
        {
            var delta = ExtractXiaomiMimoChoiceText(root, "delta");
            if (string.IsNullOrEmpty(delta))
                continue;

            transcript.Append(delta);
            yield return new OpenAITranscriptionTextDelta { Delta = delta };
        }

        yield return new OpenAITranscriptionTextDone { Text = transcript.ToString() };
    }

    private XiaomiMimoPreparedTranscription PrepareXiaomiMimoTranscription(
        TranscriptionRequest request,
        bool streaming)
    {
        if (!string.Equals(request.Model?.Trim(), XiaomiMimoAsrModel, StringComparison.Ordinal))
            throw new ArgumentException($"Xiaomi MiMo transcription only supports model '{XiaomiMimoAsrModel}'.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var format = request.MediaType.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            _ => throw new ArgumentException("Xiaomi MiMo ASR only supports MP3 and WAV audio.", nameof(request))
        };
        var canonicalMediaType = format == "mp3" ? "audio/mpeg" : "audio/wav";
        var audio = NormalizeXiaomiMimoAudio(request.Audio, canonicalMediaType, format);
        var metadata = request.GetProviderMetadata<XiaomiMimoAsrProviderMetadata>(GetIdentifier());
        var language = string.IsNullOrWhiteSpace(metadata?.Language) ? "auto" : metadata.Language.Trim().ToLowerInvariant();
        if (language is not "auto" and not "zh" and not "en")
            throw new ArgumentException("Xiaomi MiMo ASR language must be one of: auto, zh, en.", nameof(request));

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt" });
        if (metadata?.Temperature is not null)
            warnings.Add(new { type = "unsupported", feature = "temperature" });
        if (metadata?.TimestampGranularities?.Length > 0)
            warnings.Add(new { type = "unsupported", feature = "timestamp_granularities" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = XiaomiMimoAsrModel,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = audio, format }
                        }
                    }
                }
            },
            ["asr_options"] = new { language },
            ["stream"] = streaming
        };

        return new XiaomiMimoPreparedTranscription(payload, language, warnings);
    }

    private static string NormalizeXiaomiMimoAudio(object audio, string mediaType, string format)
    {
        var value = audio switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        value = value.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Xiaomi MiMo audio data URL must contain base64 data.", nameof(audio));

            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            var suppliedMediaType = (semicolon < 0 ? header : header[..semicolon]).Trim().ToLowerInvariant();
            var suppliedFormat = suppliedMediaType switch
            {
                "audio/mpeg" or "audio/mp3" => "mp3",
                "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
                _ => string.Empty
            };
            if (!string.Equals(suppliedFormat, format, StringComparison.Ordinal))
                throw new ArgumentException("Audio data URL MIME type must match MediaType.", nameof(audio));

            ValidateXiaomiMimoBase64(value[(comma + 1)..], audio);
            return value;
        }

        ValidateXiaomiMimoBase64(value, audio);
        return $"data:{mediaType};base64,{value}";
    }

    private static void ValidateXiaomiMimoBase64(string value, object audio)
    {
        try
        {
            Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be valid base64.", nameof(audio), exception);
        }
    }

    private static HttpRequestMessage CreateXiaomiMimoAudioRequest(string requestBody, bool streaming)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : MediaTypeNames.Application.Json));
        return request;
    }

    private async IAsyncEnumerable<JsonElement> SendXiaomiMimoAudioStreamAsync(
        string requestBody,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateXiaomiMimoAudioRequest(requestBody, streaming: true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Xiaomi MiMo streaming {operation} request failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                var data = string.Join("\n", dataLines).Trim();
                dataLines.Clear();
                if (data.Length == 0)
                    continue;
                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;
                yield return ParseXiaomiMimoStreamEvent(data, operation);
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].TrimStart());
        }

        var finalData = string.Join("\n", dataLines).Trim();
        if (finalData.Length > 0 && !string.Equals(finalData, "[DONE]", StringComparison.OrdinalIgnoreCase))
            yield return ParseXiaomiMimoStreamEvent(finalData, operation);
    }

    private static JsonElement ParseXiaomiMimoStreamEvent(string data, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"Xiaomi MiMo streaming {operation} returned an error: {error.GetRawText()}");
            return root;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Failed to parse Xiaomi MiMo streaming {operation} SSE JSON event: {data}", exception);
        }
    }

    private static string? ExtractXiaomiMimoChoiceText(JsonElement root, string messageProperty)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty(messageProperty, out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }

        return null;
    }

    private static float? TryGetXiaomiMimoSingle(JsonElement root, string objectName, string propertyName)
    {
        if (root.TryGetProperty(objectName, out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && nested.TryGetProperty(propertyName, out var value)
            && value.TryGetSingle(out var result))
            return result;
        return null;
    }

    private sealed record XiaomiMimoPreparedTranscription(
        Dictionary<string, object?> Payload,
        string Language,
        List<object> Warnings);

    private sealed class XiaomiMimoAsrProviderMetadata
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }

        [JsonPropertyName("temperature")]
        public float? Temperature { get; set; }

        [JsonPropertyName("timestamp_granularities")]
        public string[]? TimestampGranularities { get; set; }
    }
}
