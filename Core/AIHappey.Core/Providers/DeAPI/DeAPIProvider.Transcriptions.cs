using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<TranscriptionResponse> DeapiTranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = new Dictionary<string, object?> { ["model"] = request.Model, ["include_ts"] = false };
        MergeProviderMetadata(payload, metadata);
        var sourceUrl = metadata.TryGetString("source_url");
        using var form = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            payload["source_url"] = sourceUrl;
        }
        else
        {
            var value = request.Audio is JsonElement e && e.ValueKind == JsonValueKind.String ? e.GetString() : request.Audio?.ToString();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                payload["source_url"] = value;
            else
            {
                var bytes = DecodeBase64Payload(value ?? throw new ArgumentException("Audio or source_url is required.", nameof(request)));
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
                var extension = request.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    ? GetVideoExtension(request.MediaType) : GetAudioExtension(request.MediaType);
                form.Add(file, "source_file", "input" + extension);
            }
        }
        AddFormValues(form, payload, "source_file");
        var requestId = await SubmitMultipartJobAsync("api/v2/audio/transcriptions", form, cancellationToken);

        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var text = ExtractResultText(completed) ?? string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Language = payload.TryGetValue("lang", out var language) ? language?.ToString() : null,
            Segments = [],
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

    private static string GetAudioExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            "audio/aac" => ".aac",
            _ => ".bin"
        };
    }

    private static string GetVideoExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            "video/mpeg" => ".mpeg",
            "video/ogg" => ".ogv",
            _ => ".bin"
        };
    }
}

