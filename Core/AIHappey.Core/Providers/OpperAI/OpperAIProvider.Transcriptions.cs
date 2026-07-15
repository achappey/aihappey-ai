using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private async Task<TranscriptionResponse> OpperAITranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerOptions = GetOpperAIProviderOptions(request.ProviderOptions);
        var payload = BuildOpperAITranscriptionPayload(request, providerOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/audio/transcriptions")
        {
            Content = CreateOpperAIJsonContent(payload)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI transcription failed ({(int)response.StatusCode})."
                : $"OpperAI transcription failed ({(int)response.StatusCode}): {raw}");

        using var document = System.Text.Json.JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        return new TranscriptionResponse
        {
            Text = TryGetOpperAIString(root, "text") ?? string.Empty,
            Language = TryGetOpperAIString(root, "language"),
            DurationInSeconds = TryGetOpperAIDouble(root, "duration", "durationInSeconds") is { } duration ? (float?)duration : null,
            Segments = ExtractOpperAITranscriptionSegments(root),
            Warnings = [],
            ProviderMetadata = CreateOpperAIMediaMetadata(null),
            Response = new()
            {
                Timestamp = ResolveOpperAITimestamp(root, now),
                Headers = response.GetHeaders(),
                ModelId = (TryGetOpperAIString(root, "model") ?? request.Model).ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new()
            {
                Body = System.Text.Json.JsonSerializer.Serialize(payload, OpperAIMediaJsonOptions)
            }
        };
    }

    private static Dictionary<string, object?> BuildOpperAITranscriptionPayload(
        TranscriptionRequest request,
        Dictionary<string, object?> providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["audio"] = NormalizeOpperAIAudioInput(request.Audio, request.MediaType)
        };

        AddOpperAIParameters(payload, providerOptions);
        return payload;
    }

    private static string NormalizeOpperAIAudioInput(object audio, string mediaType)
    {
        var value = audio switch
        {
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } json => json.GetString(),
            _ => audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.");

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file_", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return value.ToDataUrl(mediaType);
    }

    private static IEnumerable<TranscriptionSegment> ExtractOpperAITranscriptionSegments(System.Text.Json.JsonElement root)
    {
        if (!TryGetOpperAIProperty(root, "segments", out var segments) || segments.ValueKind != System.Text.Json.JsonValueKind.Array)
            return [];

        return segments.EnumerateArray()
            .Select(segment => new TranscriptionSegment
            {
                Text = TryGetOpperAIString(segment, "speaker") is { } speaker && !string.IsNullOrWhiteSpace(speaker)
                    ? $"{speaker}: {TryGetOpperAIString(segment, "text") ?? string.Empty}"
                    : TryGetOpperAIString(segment, "text") ?? string.Empty,
                StartSecond = TryGetOpperAIDouble(segment, "start", "startSecond") is { } start ? (float)start : 0,
                EndSecond = TryGetOpperAIDouble(segment, "end", "endSecond") is { } end ? (float)end : 0
            })
            .ToArray();
    }
}
