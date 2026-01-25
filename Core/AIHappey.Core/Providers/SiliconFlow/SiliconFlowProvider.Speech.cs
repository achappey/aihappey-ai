using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var voice = string.IsNullOrWhiteSpace(request.Voice)
            ? "fishaudio/fish-speech-1.5:alex"
            : request.Voice.Trim();

        var responseFormat = request.OutputFormat?.Trim().ToLowerInvariant();
        var speed = request.Speed;

        var gain = ResolveProviderOptionFloat(request, "gain");
        var sampleRate = ResolveProviderOptionInt(request, "sample_rate");

        var payload = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(request.Model))
            payload["model"] = request.Model.Trim();

        if (!string.IsNullOrWhiteSpace(request.Text))
            payload["input"] = request.Text;

        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice"] = voice;

        if (!string.IsNullOrWhiteSpace(responseFormat))
            payload["response_format"] = responseFormat;

        if (sampleRate is not null)
            payload["sample_rate"] = sampleRate;

        if (speed is not null)
            payload["speed"] = speed;

        if (gain is not null)
            payload["gain"] = gain;

        payload["stream"] = false;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"SiliconFlow TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                MimeType = mime,
                Base64 = audio,
                Format = responseFormat ?? "mp3"
            },
            Warnings = warnings,
            Response = new()
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
            "wav" => "audio/wav",
            "pcm" => contentType ?? "application/octet-stream",
            _ => contentType ?? "application/octet-stream"
        };
    }

    private static float? ResolveProviderOptionFloat(SpeechRequest request, string propertyName)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("siliconflow", out var root))
            return null;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetSingle(out var singleValue) => singleValue,
            _ => null
        };
    }

    private static int? ResolveProviderOptionInt(SpeechRequest request, string propertyName)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("siliconflow", out var root))
            return null;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
            _ => null
        };
    }
}
