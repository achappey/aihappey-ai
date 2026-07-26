using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (bytes, mediaType) = DecodeAIgatewayBase64(request.Audio, request.MediaType);
        var result = await TranscribeAsync(request.Model, bytes, mediaType, request.ProviderOptions, cancellationToken);
        return ToTranscriptionResponse(result, request.Model);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var result = await TranscribeAsync(options.Model, memory.ToArray(), options.File.ContentType ?? "audio/mpeg", null, cancellationToken,
            new Dictionary<string, object?>
            {
                ["language"] = options.Language, ["prompt"] = options.Prompt, ["response_format"] = options.ResponseFormat,
                ["temperature"] = options.Temperature, ["timestamp_granularities[]"] = options.TimestampGranularities
            });
        return ToOpenAITranscriptionResponse(result.Root, options.ResponseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.Text)) yield return new OpenAITranscriptionTextDelta { Delta = response.Text };
        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    private async Task<AIgatewayTranscriptionResult> TranscribeAsync(string model, byte[] audio, string mediaType,
        Dictionary<string, JsonElement>? providerOptions, CancellationToken cancellationToken, Dictionary<string, object?>? knownOptions = null)
    {
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(file, "file", ResolveAIgatewayAudioFileName(mediaType));
        form.Add(new StringContent(model), "model");

        var values = CreateAIgatewayPayload(knownOptions ?? [], providerOptions, "file", "model");
        foreach (var value in values)
        {
            if (value.Value is null) continue;
            if (value.Value is IEnumerable<string> strings)
            {
                foreach (var item in strings) form.Add(new StringContent(item), value.Key);
            }
            else
            {
                form.Add(new StringContent(value.Value is JsonElement json ? json.GetRawText() : Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture)!), value.Key);
            }
        }
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var root = await ReadAIgatewayJsonAsync(response, "transcription", cancellationToken);
        return new AIgatewayTranscriptionResult(root, response.GetHeaders());
    }

    private TranscriptionResponse ToTranscriptionResponse(AIgatewayTranscriptionResult result, string requestedModel)
    {
        var root = result.Root;
        var segments = root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array
            ? segmentsElement.EnumerateArray().Select(segment => new TranscriptionSegment
            {
                Text = GetAIgatewayString(segment, "text") ?? string.Empty,
                StartSecond = segment.TryGetProperty("start", out var start) && start.TryGetSingle(out var startValue) ? startValue : 0,
                EndSecond = segment.TryGetProperty("end", out var end) && end.TryGetSingle(out var endValue) ? endValue : 0
            }).ToList()
            : [];
        return new TranscriptionResponse
        {
            Text = GetAIgatewayString(root, "text") ?? string.Empty,
            Language = GetAIgatewayString(root, "language"),
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var durationValue) ? durationValue : null,
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = (GetAIgatewayString(root, "model") ?? requestedModel).ToModelId(GetIdentifier()), Body = root }
        };
    }

    private static IOpenAITranscriptionResponse ToOpenAITranscriptionResponse(JsonElement root, string? responseFormat)
    {
        var text = GetAIgatewayString(root, "text") ?? string.Empty;
        if (string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase) || string.Equals(responseFormat, "diarized_json", StringComparison.OrdinalIgnoreCase))
        {
            var duration = root.TryGetProperty("duration", out var durationElement) && durationElement.TryGetDouble(out var durationValue) ? durationValue : 0;
            return new OpenAITranscriptionVerboseResponse { Text = text, Language = GetAIgatewayString(root, "language") ?? string.Empty, Duration = duration };
        }
        return new OpenAITranscriptionResponse { Text = text };
    }

    private sealed record AIgatewayTranscriptionResult(JsonElement Root, Dictionary<string, string> Headers);
}
