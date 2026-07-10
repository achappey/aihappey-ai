using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly Uri InfronSpeechUri = new("https://media.onerouter.pro/v1/audios/generations");

    private static readonly JsonSerializerOptions InfronSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> InfronSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildInfronSpeechPayload(request, metadata, warnings);
        var responseFormat = ReadInfronString(payload["response_format"]) ?? "mp3";
        var json = JsonSerializer.Serialize(payload, InfronSpeechJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InfronSpeechUri)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        ApplyAuthHeader();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Infron speech request failed ({(int)resp.StatusCode})."
                : $"Infron speech request failed ({(int)resp.StatusCode}): {raw}");
        }

        using var createDoc = JsonDocument.Parse(raw);
        var createRoot = createDoc.RootElement.Clone();
        var terminal = await WaitForInfronMediaTaskAsync("audios", createRoot, metadata, cancellationToken);

        if (!IsInfronMediaSuccessStatus(terminal.Status) && !HasInfronMediaOutputs(terminal.Root))
            throw new InvalidOperationException($"Infron speech generation failed with status '{terminal.Status}': {GetInfronMediaError(terminal.Root)}");

        var audioUrl = TryGetFirstInfronAudioUrl(terminal.Root)
            ?? throw new InvalidOperationException("No valid audio outputs returned from Infron audio API.");

        if (audioUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var mimeType = TryReadDataUrlMediaType(audioUrl) ?? ResolveInfronSpeechMimeType(responseFormat);

            return new SpeechResponse
            {
                Audio = new SpeechAudioResponse
                {
                    Base64 = ExtractBase64Payload(audioUrl),
                    MimeType = mimeType,
                    Format = responseFormat
                },
                Request = new()
                {
                    Body = payload
                },
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = ResolveInfronTimestamp(terminal.Root, now),
                    ModelId = terminal.Root.TryGetString("model")?.ToModelId(GetIdentifier())
                         ?? request.Model.ToModelId(GetIdentifier())
                }
            };
        }

        var fallbackMimeType = GuessInfronAudioMediaType(audioUrl) ?? ResolveInfronSpeechMimeType(responseFormat);
        var (Base64, MediaType, ContentLength) = await DownloadInfronMediaAsync(audioUrl, fallbackMimeType, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Base64,
                MimeType = MediaType,
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = ResolveInfronTimestamp(terminal.Root, now),
                ModelId = terminal.Root.TryGetString("model")?.ToModelId(GetIdentifier())
                         ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildInfronSpeechPayload(SpeechRequest request, JsonElement? metadata, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var voice = request.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            voice = ReadInfronMediaString(metadata, "voice");

        if (string.IsNullOrWhiteSpace(voice))
            voice = "alloy";

        var responseFormat = request.OutputFormat?.Trim();

        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = ReadInfronMediaString(metadata, "response_format", "responseFormat", "format");

        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = "mp3";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = responseFormat
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            payload["instructions"] = request.Instructions;

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        MergeInfronProviderOptions(
                payload,
                metadata,
                new HashSet<string>
                {
                    "voice",
                    "response_format",
                    "responseFormat",
                    "format",
                    "poll_url",
                    "pollUrl",
                    "task_url",
                    "taskUrl",
                    "poll_interval_seconds",
                    "pollIntervalSeconds",
                    "poll_timeout_minutes",
                    "pollTimeoutMinutes",
                    "poll_max_attempts",
                    "pollMaxAttempts"
                });

        return payload;
    }

    private Dictionary<string, JsonElement> BuildInfronSpeechProviderMetadata(
        SpeechRequest request,
        Dictionary<string, object?> payload,
        JsonElement createRoot,
        InfronMediaTaskResult terminal,
        string? contentType,
        long? contentLength,
        HttpResponseMessage response)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["endpoint"] = InfronSpeechUri.ToString(),
            ["request"] = payload,
            ["create"] = createRoot,
            ["retrieve"] = terminal.Root,
            ["taskId"] = terminal.TaskId,
            ["status"] = terminal.Status,
            ["response"] = new
            {
                statusCode = (int)response.StatusCode,
                contentType,
                contentLength
            }
        };

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var rawOptions)
            && rawOptions.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            metadata["providerOptions"] = rawOptions.Clone();
        }

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, InfronSpeechJsonOptions)
        };
    }

    private static string ResolveInfronSpeechMimeType(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return MediaTypeNames.Application.Octet;

        return responseFormat.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            var mime when mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => mime,
            _ => MediaTypeNames.Application.Octet
        };
    }

    private static string? TryGetFirstInfronAudioUrl(JsonElement root)
    {
        foreach (var item in EnumerateInfronMediaOutputItems(root))
        {
            var url = TryGetInfronMediaUrl(item, "audio_url", "audioUrl");
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            if (item.ValueKind == JsonValueKind.Object)
            {
                var b64 = item.TryGetString("b64_json")
                    ?? item.TryGetString("base64")
                    ?? item.TryGetString("data");

                if (!string.IsNullOrWhiteSpace(b64))
                {
                    var mediaType = GuessInfronAudioMediaType(item) ?? "audio/mpeg";
                    return $"data:{mediaType};base64,{ExtractBase64Payload(b64)}";
                }
            }
        }

        return null;
    }

    private static string? GuessInfronAudioMediaType(JsonElement item)
    {
        var value = item.TryGetString("mime_type")
            ?? item.TryGetString("mimeType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("format")
            ?? item.TryGetString("output_format");

        return NormalizeInfronAudioMediaType(value);
    }

    private static string? GuessInfronAudioMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".opus" => "audio/opus",
            ".ogg" => "audio/ogg",
            _ => null
        };
    }

    private static string? NormalizeInfronAudioMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "opus" => "audio/opus",
            "ogg" => "audio/ogg",
            var mime when mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => mime,
            _ => null
        };
    }

    private static string? ReadInfronString(object? value)
    {
        return value switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString()?.Trim(),
            JsonElement { ValueKind: JsonValueKind.Number } el => el.GetRawText(),
            _ => value.ToString()
        };
    }

}
