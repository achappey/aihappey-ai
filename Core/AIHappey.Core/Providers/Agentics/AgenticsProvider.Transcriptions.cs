using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    private async Task<TranscriptionResponse> AgenticsTranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Audio?.ToString()))
            throw new ArgumentException("Audio base64 payload is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var bytes = DecodeAgenticsBase64Payload(request.Audio.ToString()!);
        var mediaType = NormalizeAgenticsAudioMediaType(request.MediaType);
        var language = TryGetAgenticsString(metadata, "language");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", "audio" + GetAgenticsAudioExtension(mediaType));

        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        using var response = await _client.PostAsync("v1/audio/stt", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agentics transcription failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var text = TryGetAgenticsString(root, "text") ?? string.Empty;
        var detectedLanguage = TryGetAgenticsString(root, "language") ?? language;
        var duration = TryGetDouble(root, "durationMs") is { } durationMs
            ? (float?)(durationMs / 1000d)
            : TryGetFloat(root, "durationInSeconds", "duration", "durationSeconds");

        return new TranscriptionResponse
        {
            Text = text,
            Language = detectedLanguage,
            DurationInSeconds = duration,
            Segments = [],
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

}
