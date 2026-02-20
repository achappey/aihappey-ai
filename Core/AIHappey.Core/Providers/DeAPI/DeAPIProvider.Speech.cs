using System.Net.Mime;
using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<SpeechResponse> DeapiSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var voice = request.Voice ?? TryGetString(metadata, "voice") ?? "af_sky";
        var lang = request.Language ?? TryGetString(metadata, "lang") ?? "en-us";
        var speed = request.Speed ?? (float?)(TryGetNumber(metadata, "speed") ?? 1.0);
        var format = request.OutputFormat ?? TryGetString(metadata, "format") ?? "flac";
        var sampleRate = (int)(TryGetNumber(metadata, "sample_rate") ?? 24000);
        var webhookUrl = TryGetString(metadata, "webhook_url") ?? TryGetString(metadata, "webhookUrl");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model"] = request.Model,
            ["voice"] = voice,
            ["lang"] = lang,
            ["speed"] = speed,
            ["format"] = format,
            ["sample_rate"] = sampleRate,
            ["webhook_url"] = webhookUrl
        };

        var requestId = await SubmitJsonJobAsync("api/v1/client/txt2audio", payload, cancellationToken);
        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var resultUrl = GetResultUrl(completed)
            ?? throw new InvalidOperationException($"DeAPI speech result_url missing for request {requestId}.");

        var fallbackMime = ResolveAudioMimeType(format);
        var (bytesOut, mimeType) = await DownloadResultAsync(resultUrl, fallbackMime, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytesOut),
                MimeType = mimeType,
                Format = format
            },
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new { requestId, resultUrl }
            }
        };
    }

    private static string ResolveAudioMimeType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => MediaTypeNames.Application.Octet
        };
    }
}

