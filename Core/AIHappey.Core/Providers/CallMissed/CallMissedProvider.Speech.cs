using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
    private static readonly JsonSerializerOptions CallMissedSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input"] = request.Text,
            ["voice"] = string.IsNullOrWhiteSpace(request.Voice) ? "alloy" : request.Voice!.Trim(),
            ["response_format"] = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat!.Trim().ToLowerInvariant()
        };

        if (request.Speed is not null)
            payload["speed"] = request.Speed.Value;

        MergeProviderOptions(payload, request.GetProviderMetadata<JsonElement>(GetIdentifier()));

        var responseFormat = payload.TryGetValue("response_format", out var responseFormatValue)
            ? ReadStringObject(responseFormatValue) ?? "mp3"
            : "mp3";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, CallMissedSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"CallMissed speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(responseFormat, contentType),
                Format = responseFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    endpoint = "v1/audio/speech",
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    responseFormat
                })
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static string ResolveSpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => contentType ?? "application/octet-stream"
        };
    }

    private static string? ReadStringObject(object? value)
    {
        return value switch
        {
            null => null,
            string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString()?.Trim(),
            _ => value.ToString()
        };
    }
}
