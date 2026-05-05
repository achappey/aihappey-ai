using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestOpenRouter(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var payload = BuildOpenRouterSpeechPayload(request);
        var responseFormat = payload.TryGetValue("response_format", out var responseFormatValue)
            ? ReadOpenRouterSpeechString(responseFormatValue) ?? "pcm"
            : "pcm";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenRouterSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"OpenRouter speech request failed ({(int)resp.StatusCode})."
                : $"OpenRouter speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var providerMetadata = BuildOpenRouterSpeechProviderMetadata(request, payload, responseFormat, contentType, bytes.LongLength, resp);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType ?? ResolveOpenRouterSpeechMimeType(responseFormat),
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
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

    private static Dictionary<string, object?> BuildOpenRouterSpeechPayload(SpeechRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["model"] = request.Model,
            ["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice.Trim(),
            ["response_format"] = string.IsNullOrWhiteSpace(request.OutputFormat) ? "pcm" : request.OutputFormat.Trim()
        };

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        MergeOpenRouterSpeechProviderOptions(payload, request);

        return payload;
    }

    private static void MergeOpenRouterSpeechProviderOptions(Dictionary<string, object?> payload, SpeechRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("openrouter", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private Dictionary<string, JsonElement> BuildOpenRouterSpeechProviderMetadata(
        SpeechRequest request,
        Dictionary<string, object?> payload,
        string responseFormat,
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
                contentLength,
                responseFormat
            }
        };

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue("openrouter", out var rawOptions)
            && rawOptions.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            metadata["providerOptions"] = rawOptions.Clone();
        }

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, OpenRouterSpeechJsonOptions)
        };
    }

    private static string? ReadOpenRouterSpeechString(object? value)
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

    private static string ResolveOpenRouterSpeechMimeType(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return "application/octet-stream";

        return responseFormat.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "pcm" => "audio/pcm",
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };
    }
}
