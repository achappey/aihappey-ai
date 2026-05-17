using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Eliza;

public partial class ElizaProvider
{
    private const string ElizaDefaultTranscriptionModel = "voice/stt";

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyVoiceApiKeyHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        var model = NormalizeElizaVoiceModel(request.Model, ElizaDefaultTranscriptionModel);
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var fileName = "audio" + request.MediaType.GetAudioExtension();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "audio", fileName);

        using var response = await _client.PostAsync("v1/voice/stt", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Eliza STT failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var text = root.TryGetString("transcript")
            ?? root.TryGetString("text")
            ?? string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            DurationInSeconds = TryGetElizaDurationSeconds(root),
            Segments = [],
            Warnings = [],
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = root
            }
        };
    }

    private static float? TryGetElizaDurationSeconds(JsonElement root)
    {
        if (TryGetElizaDouble(root, "duration_ms") is { } durationMs)
            return (float)(durationMs / 1000d);

        if (TryGetElizaDouble(root, "durationMs") is { } camelDurationMs)
            return (float)(camelDurationMs / 1000d);

        if (TryGetElizaDouble(root, "duration") is { } duration)
            return (float)duration;

        if (TryGetElizaDouble(root, "duration_seconds") is { } durationSeconds)
            return (float)durationSeconds;

        if (TryGetElizaDouble(root, "durationInSeconds") is { } camelDurationSeconds)
            return (float)camelDurationSeconds;

        return null;
    }
}
