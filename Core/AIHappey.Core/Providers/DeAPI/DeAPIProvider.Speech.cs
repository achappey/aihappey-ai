using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
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

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model"] = request.Model
        };
        if (!string.IsNullOrWhiteSpace(request.Voice)) payload["voice"] = request.Voice;
        if (!string.IsNullOrWhiteSpace(request.Language)) payload["lang"] = request.Language;
        if (request.Speed is not null) payload["speed"] = request.Speed;
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)) payload["format"] = request.OutputFormat;
        MergeProviderMetadata(payload, metadata);

        using var form = new MultipartFormDataContent();
        AddFormValues(form, payload, "ref_audio");
        var requestId = await SubmitMultipartJobAsync("api/v2/audio/speech", form, cancellationToken);
        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var resultUrl = GetResultUrl(completed)
            ?? throw new InvalidOperationException($"DeAPI speech result_url missing for request {requestId}.");

        var format = payload.TryGetValue("format", out var formatValue) ? formatValue?.ToString() : request.OutputFormat;
        var fallbackMime = ResolveAudioMimeType(format ?? "");
        var (bytesOut, mimeType) = await DownloadResultAsync(resultUrl, fallbackMime, cancellationToken);
        var resolvedFormat = ResolveAudioFormat(format, mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytesOut),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
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

    private static string ResolveAudioFormat(string? format, string mimeType)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return format;

        return mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" or "audio/opus" => "ogg",
            "audio/flac" => "flac",
            _ => throw new InvalidOperationException($"DeAPI returned audio with unsupported or missing format '{mimeType}'.")
        };
    }
}
