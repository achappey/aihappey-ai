using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly Uri InfronSpeechUri = new("https://audio.onerouter.pro/v1/audio/speech");

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
        var payload = BuildInfronSpeechPayload(request, warnings);
        var responseFormat = ReadInfronString(payload["response_format"]) ?? "mp3";
        var json = JsonSerializer.Serialize(payload, InfronSpeechJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InfronSpeechUri)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        ApplyInfronAudioAuthHeader(httpRequest);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"Infron speech request failed ({(int)resp.StatusCode})."
                : $"Infron speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType ?? ResolveInfronSpeechMimeType(responseFormat),
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = BuildInfronSpeechProviderMetadata(request, payload, contentType, bytes.LongLength, resp),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    contentLength = bytes.LongLength
                }
            }
        };
    }

    private static Dictionary<string, object?> BuildInfronSpeechPayload(SpeechRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetProviderMetadata<JsonElement>(nameof(Infron).ToLowerInvariant());
        var voice = request.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            voice = metadata.TryGetString("voice");

        if (string.IsNullOrWhiteSpace(voice))
            voice = "alloy";

        var responseFormat = request.OutputFormat?.Trim();

        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = metadata.TryGetString("response_format")
                ?? metadata.TryGetString("responseFormat")
                ?? metadata.TryGetString("format");

        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = "mp3";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
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
                    "format"
                });

        return payload;
    }

    private Dictionary<string, JsonElement> BuildInfronSpeechProviderMetadata(
        SpeechRequest request,
        Dictionary<string, object?> payload,
        string? contentType,
        long contentLength,
        HttpResponseMessage response)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["request"] = payload,
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

    private void ApplyInfronAudioAuthHeader(HttpRequestMessage httpRequest)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Infron)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("Authorization");
        httpRequest.Headers.Remove("Authorization");
        httpRequest.Headers.TryAddWithoutValidation("Authorization", key);
    }
}
