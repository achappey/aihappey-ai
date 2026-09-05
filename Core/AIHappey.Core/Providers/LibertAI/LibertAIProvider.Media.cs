using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LibertAI;

public partial class LibertAIProvider
{
    private static readonly JsonSerializerOptions LibertAIJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLibertAISpeech(request.Model, request.Text, request.OutputFormat, request.Speed);

        var payload = GetLibertAIProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        SetLibertAI(payload, "voice", request.Voice);
        payload["response_format"] = "wav";
        SetLibertAI(payload, "speed", request.Speed);

        var result = await SendLibertAISpeechAsync(payload, cancellationToken);
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(result.Audio),
                MimeType = result.MimeType,
                Format = "wav"
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Metadata),
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = result.Metadata
            },
            Request = new SpeechRequestItem { Body = payload }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateLibertAISpeech(options.Model, options.Input, options.ResponseFormat, options.Speed);
        var payload = JsonSerializer.SerializeToNode(options, LibertAIJson)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the LibertAI speech request.");
        payload.Remove("stream_format");
        payload.Remove("instructions");
        payload["response_format"] = "wav";
        var result = await SendLibertAISpeechAsync(payload, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLibertAIImage(request.Model, request.Prompt, request.N);
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("LibertAI does not document image editing.");

        var payload = GetLibertAIProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetLibertAI(payload, "size", request.Size);
        SetLibertAI(payload, "n", request.N);
        payload["response_format"] = "b64_json";

        var result = await SendLibertAIImageAsync(payload, cancellationToken);
        var response = DeserializeLibertAIImages(result.Root);
        var images = (response.Data ?? [])
            .Where(image => !string.IsNullOrWhiteSpace(image.B64Json))
            .Select(image => $"data:image/png;base64,{image.B64Json}")
            .ToArray();
        if (images.Length == 0)
            throw new InvalidOperationException("LibertAI image generation returned no images.");

        var warnings = new List<object>();
        if (request.Seed.HasValue) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Usage = response.Usage is null ? null : new ImageUsageData
            {
                InputTokens = response.Usage.InputTokens,
                OutputTokens = response.Usage.OutputTokens,
                TotalTokens = response.Usage.TotalTokens
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateLibertAIImage(options.Model, options.Prompt, options.N);
        var payload = JsonSerializer.SerializeToNode(options, LibertAIJson)?.AsObject()
            ?? throw new InvalidOperationException("Could not serialize the LibertAI image request.");
        payload.Remove("stream");
        payload["response_format"] = "b64_json";
        var result = await SendLibertAIImageAsync(payload, cancellationToken);
        var response = DeserializeLibertAIImages(result.Root);
        if (response.Created == 0) response.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        response.Size ??= options.Size;
        response.OutputFormat ??= options.OutputFormat;
        response.Quality ??= options.Quality;
        response.Background ??= options.Background;
        return response;
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(image.B64Json)) continue;
            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    private async Task<LibertAISpeechResult> SendLibertAISpeechAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payload.ToJsonString(LibertAIJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LibertAI speech request failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");
        if (audio.Length == 0) throw new InvalidOperationException("LibertAI speech request returned empty audio.");
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
        var headers = response.GetHeaders();
        var metadata = JsonSerializer.SerializeToElement(new
        {
            status_code = (int)response.StatusCode,
            content_type = mimeType,
            content_length = audio.LongLength,
            headers
        }, LibertAIJson);
        return new LibertAISpeechResult(audio, mimeType, headers, metadata);
    }

    private async Task<LibertAIJsonResult> SendLibertAIImageAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(payload.ToJsonString(LibertAIJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LibertAI image generation failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return new LibertAIJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("LibertAI image generation returned invalid JSON.", exception);
        }
    }

    private static OpenAIImagesResponse DeserializeLibertAIImages(JsonElement root)
        => JsonSerializer.Deserialize<OpenAIImagesResponse>(root.GetRawText(), LibertAIJson)
            ?? throw new InvalidOperationException("LibertAI returned an invalid image response.");

    private JsonObject GetLibertAIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object) return [];
        return JsonNode.Parse(options.GetRawText())?.AsObject() ?? [];
    }

    private static void ValidateLibertAISpeech(string model, string input, string? format, float? speed)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("Input is required.", nameof(input));
        if (input.Length > 8192) throw new ArgumentException("LibertAI speech input cannot exceed 8,192 characters.", nameof(input));
        if (!string.IsNullOrWhiteSpace(format) && !format.Equals("wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("LibertAI currently supports only WAV speech output.", nameof(format));
        if (speed is < 0.25f or > 4.0f)
            throw new ArgumentOutOfRangeException(nameof(speed), "LibertAI speech speed must be between 0.25 and 4.0.");
    }

    private static void ValidateLibertAIImage(string model, string prompt, int? count)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Prompt is required.", nameof(prompt));
        if (count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count), "LibertAI image count must be between 1 and 4.");
    }

    private static void SetLibertAI(JsonObject payload, string name, object? value)
    {
        if (value is not null) payload[name] = JsonValue.Create(value);
    }

    private sealed record LibertAISpeechResult(byte[] Audio, string MimeType, IDictionary<string, string> Headers, JsonElement Metadata);
    private sealed record LibertAIJsonResult(JsonElement Root, IDictionary<string, string> Headers);
}
