using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = ParseDevicAIRoute(request.Model);
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var audio = GetFileValue(request.Audio);
        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        var mediaType = request.MediaType;
        var bytes = DecodeFile(audio, ref mediaType, "audio");

        using var httpRequest = CreateRequest(HttpMethod.Post, "whisper");
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(content, "audio", "audio" + GetAudioExtension(mediaType));

        var options = request.ProviderOptions is not null
                      && request.ProviderOptions.TryGetValue(GetIdentifier(), out var configured)
            ? configured
            : default(JsonElement?);
        AddFormValue(form, "language", options.HasValue ? GetString(options.Value, "language") : null);
        AddFormValue(form, "messageUid", options.HasValue ? GetString(options.Value, "messageUid", "message_uid") : null);
        AddFormValue(form, "chatUid", options.HasValue ? GetString(options.Value, "chatUid", "chat_uid") : null);
        AddFormValue(form, "tenantId", options.HasValue ? GetString(options.Value, "tenantId", "tenant_id") : null);
        httpRequest.Content = form;

        var timestamp = DateTime.UtcNow;
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"DevicAI transcription failed with status {(int)response.StatusCode} ({response.StatusCode}): {rawBody}",
                null,
                response.StatusCode);

        var raw = JsonSerializer.Deserialize<JsonElement>(rawBody, DevicAIJson).Clone();
        var text = GetString(raw, "text")
                   ?? throw new InvalidOperationException("DevicAI transcription response did not include text.");
        return new TranscriptionResponse
        {
            Text = text,
            Language = GetString(raw, "language"),
            Segments = [],
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(raw),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = response.GetHeaders(),
                ModelId = "whisper".ToModelId(GetIdentifier()),
                Body = raw.Clone()
            }
        };
    }

    /// <summary>
    /// Retrieves a DevicAI transcript for internal provider use. The public model-provider
    /// abstraction has no transcript-retrieval operation, so callers receive transcriptId
    /// in transcription provider metadata and may use this helper from provider-specific code.
    /// </summary>
    internal Task<JsonElement> GetTranscriptAsync(string transcriptId, CancellationToken cancellationToken = default)
        => SendJsonAsync(
            HttpMethod.Get,
            $"whisper/{Uri.EscapeDataString(transcriptId)}",
            null,
            "get transcript",
            cancellationToken);

    private static void AddFormValue(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), name);
    }

    private static string GetAudioExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            "audio/flac" => ".flac",
            _ => ".bin"
        };
}
