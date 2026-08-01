using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Simplismart;

public partial class SimplismartProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var audio = SimplismartGetBase64(request.Audio);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = SimplismartCreatePayload(metadata);
        payload["audio_data"] = audio;
        payload.TryAdd("language", "en");
        payload.TryAdd("task", "transcribe");

        var result = await SimplismartPostJsonAsync(SimplismartWhisperEndpoint(request.Model), payload, cancellationToken);
        var transcription = SimplismartReadStringArray(result.Body, "transcription");
        var text = string.Join("", transcription);
        var segments = SimplismartReadSegments(result.Body);
        if (string.IsNullOrWhiteSpace(text))
            text = string.Join("", segments.Select(segment => segment.Text));

        return new TranscriptionResponse
        {
            Text = text,
            Language = SimplismartReadString(result.Body, "language"),
            DurationInSeconds = SimplismartReadFloat(result.Body, "request_time"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Body),
            Request = new TranscriptionRequestItem { Body = JsonSerializer.Serialize(payload) },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Body
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var metadata = new Dictionary<string, JsonElement>();
        if (!string.IsNullOrWhiteSpace(options.Language)) metadata["language"] = JsonSerializer.SerializeToElement(options.Language);
        if (!string.IsNullOrWhiteSpace(options.Prompt)) metadata["initial_prompt"] = JsonSerializer.SerializeToElement(options.Prompt);
        metadata["streaming"] = JsonSerializer.SerializeToElement(false);
        request.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata) };
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(options.ResolveOpenAITranscriptionResponseFormat());
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private static string SimplismartWhisperEndpoint(string model)
        => model.Contains("whisper-v2", StringComparison.OrdinalIgnoreCase)
            ? "model/v2/infer/whisper"
            : "model/infer/whisper";

    private static string SimplismartGetBase64(object? value)
    {
        var audio = value is JsonElement { ValueKind: JsonValueKind.String } element ? element.GetString() : value?.ToString();
        if (string.IsNullOrWhiteSpace(audio)) throw new ArgumentException("Audio is required.");
        var comma = audio.IndexOf(',');
        if (audio.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0) audio = audio[(comma + 1)..];
        Convert.FromBase64String(audio);
        return audio;
    }

    private static TranscriptionSegment[] SimplismartReadSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => new TranscriptionSegment
        {
            Text = SimplismartReadString(x, "text") ?? string.Empty,
            StartSecond = SimplismartReadFloat(x, "start") ?? 0,
            EndSecond = SimplismartReadFloat(x, "end") ?? 0
        }).ToArray();
    }
}
