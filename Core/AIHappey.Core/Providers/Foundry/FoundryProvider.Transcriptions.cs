using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace AIHappey.Core.Providers.Foundry;

public partial class FoundryProvider
{
    private const string FoundryTranscriptionEndpoint = "openai/v1/audio/transcriptions?api-version=preview";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var audioValue = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioValue))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audio = Convert.FromBase64String(audioValue.RemoveDataUrlPrefix());
        var metadata = request.GetProviderMetadata<OpenAiTranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var requestFields = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["response_format"] = "verbose_json",
            ["language"] = metadata?.Language,
            ["prompt"] = metadata?.Prompt,
            ["temperature"] = metadata?.Temperature,
            ["timestamp_granularities"] = metadata?.TimestampGranularities
        };

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(audio);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(request.MediaType) ? "application/octet-stream" : request.MediaType);
        form.Add(fileContent, "file", "audio" + request.MediaType.GetAudioExtension());
        FoundryAddMultipartString(form, "model", request.Model);
        FoundryAddMultipartString(form, "response_format", "verbose_json");
        FoundryAddMultipartString(form, "language", metadata?.Language);
        FoundryAddMultipartString(form, "prompt", metadata?.Prompt);
        FoundryAddMultipartString(form, "temperature", metadata?.Temperature?.ToString(CultureInfo.InvariantCulture));

        if (metadata?.TimestampGranularities is not null)
            foreach (var granularity in metadata.TimestampGranularities)
                FoundryAddMultipartString(form, "timestamp_granularities[]", granularity);

        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, FoundryTranscriptionEndpoint)
        {
            Content = form
        };
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Foundry transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Foundry transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        return new TranscriptionResponse
        {
            Text = FoundryReadString(root, "text") ?? string.Empty,
            Language = FoundryReadString(root, "language"),
            DurationInSeconds = FoundryReadFloat(root, "duration"),
            Segments = FoundryReadTranscriptionSegments(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem
            {
                Body = JsonSerializer.Serialize(requestFields)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            FoundryTranscriptionEndpoint,
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            FoundryTranscriptionEndpoint,
            cancellationToken);
    }

    private static void FoundryAddMultipartString(MultipartFormDataContent form, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static IEnumerable<TranscriptionSegment> FoundryReadTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return [];

        return segments.EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Object)
            .Select(segment => new TranscriptionSegment
            {
                Text = FoundryReadString(segment, "text") ?? string.Empty,
                StartSecond = FoundryReadFloat(segment, "start") ?? 0,
                EndSecond = FoundryReadFloat(segment, "end") ?? 0
            })
            .ToArray();
    }

    private static string? FoundryReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? FoundryReadFloat(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetSingle(out var number) => number,
            JsonValueKind.String when float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }
}
