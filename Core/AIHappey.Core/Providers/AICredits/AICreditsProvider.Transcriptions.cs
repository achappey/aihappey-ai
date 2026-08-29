using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.AICredits;

public partial class AICreditsProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (request.Audio is null) throw new ArgumentException("Audio is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType)) throw new ArgumentException("MediaType is required.", nameof(request));

        var payload = GetAICreditsOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input_audio"] = new JsonObject
        {
            ["data"] = NormalizeAICreditsBase64(request.Audio.ToString()),
            ["format"] = ResolveAICreditsAudioInputFormat(request.MediaType)
        };

        var result = await SendAICreditsTranscriptionAsync(payload, cancellationToken);
        return CreateAICreditsTranscriptionResponse(result, request.Model, payload);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);

        var payload = GetAICreditsOptions(options.AdditionalProperties);
        payload["model"] = options.Model;
        payload["input_audio"] = new JsonObject
        {
            ["data"] = Convert.ToBase64String(memory.ToArray()),
            ["format"] = ResolveAICreditsAudioInputFormat(options.File.ContentType, options.File.FileName)
        };
        if (!string.IsNullOrWhiteSpace(options.Language)) payload["language"] = options.Language;
        if (!string.IsNullOrWhiteSpace(options.Prompt)) payload["prompt"] = options.Prompt;
        if (options.Temperature.HasValue) payload["temperature"] = options.Temperature.Value;

        var result = await SendAICreditsTranscriptionAsync(payload, cancellationToken);
        var response = CreateAICreditsTranscriptionResponse(result, options.Model, payload);
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

    private async Task<AICreditsJsonResult> SendAICreditsTranscriptionAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var body = payload.ToJsonString(AICreditsSpeechJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AICredits transcription request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new AICreditsJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private TranscriptionResponse CreateAICreditsTranscriptionResponse(
        AICreditsJsonResult result,
        string model,
        JsonObject requestPayload)
    {
        var root = result.Root;
        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var text = GetAICreditsString(segment, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = GetAICreditsSingle(segment, "start") ?? 0,
                    EndSecond = GetAICreditsSingle(segment, "end") ?? 0
                });
            }
        }

        var textResult = GetAICreditsString(root, "text") ?? string.Join(" ", segments.Select(static segment => segment.Text));
        return new TranscriptionResponse
        {
            Text = textResult,
            Language = GetAICreditsString(root, "language"),
            DurationInSeconds = GetAICreditsSingle(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem { Body = requestPayload.ToJsonString(AICreditsSpeechJsonOptions) }
        };
    }

    private static string NormalizeAICreditsBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Audio is required.", nameof(value));
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? value[(marker + 8)..] : value;
    }

    private static string ResolveAICreditsAudioInputFormat(string? mediaType, string? fileName = null)
    {
        var extension = Path.GetExtension(fileName)?.TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(extension)) return extension;
        return mediaType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/mp4" or "audio/x-m4a" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            "audio/flac" => "flac",
            var value when !string.IsNullOrWhiteSpace(value) && value.Contains('/') => value.Split('/').Last(),
            _ => "mp3"
        };
    }

    private static string? GetAICreditsString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static float? GetAICreditsSingle(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetSingle(out var number) ? number : null;
}
