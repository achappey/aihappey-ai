using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.MCP.Media;
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
        var includeTs = TryGetBoolean(metadata, "include_ts") ?? true;
        var returnInResponse = TryGetBoolean(metadata, "return_result_in_response") ?? true;
        var webhookUrl = TryGetString(metadata, "webhook_url") ?? TryGetString(metadata, "webhookUrl");
        var language = TryGetString(metadata, "language");
        var format = TryGetString(metadata, "format");
        var videoUrl = TryGetString(metadata, "video_url") ?? TryGetString(metadata, "videoUrl");
        var audioUrl = TryGetString(metadata, "audio_url") ?? TryGetString(metadata, "audioUrl");

        var endpoint = ResolveTranscriptionEndpoint(request.MediaType, videoUrl, audioUrl, request.Model);
        string requestId;

        if (endpoint is "api/v1/client/img2txt" or "api/v1/client/videofile2txt" or "api/v1/client/audiofile2txt")
        {
            var payloadBytes = DecodeBase64Payload(request.Audio.ToString() ?? string.Empty);
            using var form = new MultipartFormDataContent();

            var fieldName = endpoint switch
            {
                "api/v1/client/img2txt" => "image",
                "api/v1/client/videofile2txt" => "video",
                _ => "audio"
            };

            var fileName = endpoint switch
            {
                "api/v1/client/img2txt" => "input" + GetImageExtension(request.MediaType),
                "api/v1/client/videofile2txt" => "input" + GetVideoExtension(request.MediaType),
                _ => "input" + GetAudioExtension(request.MediaType)
            };

            var fileContent = new ByteArrayContent(payloadBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
            form.Add(fileContent, fieldName, fileName);
            form.Add(new StringContent(request.Model), "model");

            if (endpoint != "api/v1/client/img2txt")
                form.Add(new StringContent(includeTs ? "true" : "false"), "include_ts");

            if (!string.IsNullOrWhiteSpace(language))
                form.Add(new StringContent(language), "language");
            if (!string.IsNullOrWhiteSpace(format))
                form.Add(new StringContent(format), "format");
            if (!string.IsNullOrWhiteSpace(webhookUrl))
                form.Add(new StringContent(webhookUrl), "webhook_url");
            if (returnInResponse)
                form.Add(new StringContent("true"), "return_result_in_response");

            requestId = await SubmitMultipartJobAsync(endpoint, form, cancellationToken);
        }
        else
        {
            var payload = new Dictionary<string, object?>
            {
                ["model"] = request.Model,
                ["include_ts"] = includeTs,
                ["return_result_in_response"] = returnInResponse,
                ["webhook_url"] = webhookUrl
            };

            if (endpoint == "api/v1/client/vid2txt")
                payload["video_url"] = videoUrl ?? request.Audio.ToString();
            else
                payload["audio_url"] = audioUrl ?? request.Audio.ToString();

            requestId = await SubmitJsonJobAsync(endpoint, payload, cancellationToken);
        }

        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var text = ExtractResultText(completed) ?? string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            Segments = [],
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new { requestId, endpoint, status = "done" }
            }
        };
    }

    private static string ResolveTranscriptionEndpoint(string mediaType, string? videoUrl, string? audioUrl, string model)
    {
        if (string.Equals(model, "Nanonets_Ocr_S_F16", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return "api/v1/client/img2txt";

        if (!string.IsNullOrWhiteSpace(videoUrl))
            return "api/v1/client/vid2txt";

        if (!string.IsNullOrWhiteSpace(audioUrl))
            return "api/v1/client/aud2txt";

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return "api/v1/client/videofile2txt";

        return "api/v1/client/audiofile2txt";
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

