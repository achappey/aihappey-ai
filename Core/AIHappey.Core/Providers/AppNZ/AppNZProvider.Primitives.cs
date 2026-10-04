using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        return (await this.OpenAICompatibleEmbeddingRequestAsync(client, request, cancellationToken: cancellationToken)).Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(client,
            request.ToOpenAIEmbeddingRequest(GetIdentifier()), cancellationToken: cancellationToken);
        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata(result.Response));
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        return await client.OpenAICompatibleImageGenerationRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // AppNZ documents image generation, not an image SSE contract.
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
        foreach (var image in response.Data ?? [])
        {
            var base64 = image.B64Json;
            if (string.IsNullOrWhiteSpace(base64) && image.Url is { } url)
            {
                using var download = await _downloadClient.GetAsync(url, cancellationToken);
                download.EnsureSuccessStatusCode();
                base64 = Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync(cancellationToken));
            }
            if (!string.IsNullOrWhiteSpace(base64))
                yield return new OpenAIImageGenerationCompleted
                {
                    B64Json = base64, CreatedAt = response.Created, Usage = response.Usage,
                    OutputFormat = response.OutputFormat ?? options.OutputFormat,
                    Size = response.Size ?? options.Size, Quality = response.Quality ?? options.Quality
                };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AppNZ does not document an OpenAI image-edit endpoint.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("AppNZ does not document an OpenAI image-edit endpoint.");

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("AppNZ does not document an OpenAI image-edit endpoint.");

        var payload = GetMediaOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["response_format"] = "b64_json";
        if (request.N is not null) payload["n"] = request.N;
        if (request.Size is not null) payload["size"] = request.Size;
        if (request.AspectRatio is not null) payload["aspect_ratio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed;

        using var client = CreateClient();
        var raw = await SendJsonAsync(client, HttpMethod.Post, "v1/images/generations", payload, cancellationToken);
        var result = raw.Deserialize<OpenAIImagesResponse>(Json)
            ?? throw new InvalidOperationException("AppNZ returned an empty image response.");
        var images = new List<string>();
        foreach (var image in result.Data ?? [])
        {
            if (!string.IsNullOrWhiteSpace(image.B64Json))
                images.Add(image.B64Json);
            else if (image.Url is { } url)
            {
                using var download = await _downloadClient.GetAsync(url, cancellationToken);
                download.EnsureSuccessStatusCode();
                images.Add(Convert.ToBase64String(await download.Content.ReadAsByteArrayAsync(cancellationToken)));
            }
        }
        return new ImageResponse
        {
            Images = images, ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(raw),
            Usage = result.Usage is null ? null : new ImageUsageData
            {
                InputTokens = result.Usage.InputTokens, OutputTokens = result.Usage.OutputTokens, TotalTokens = result.Usage.TotalTokens
            },
            Response = CreateMediaResponse(request.Model)
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        var scoped = GetMediaOptions(options.AdditionalProperties);
        var flattened = (options.AdditionalProperties ?? [])
            .Where(p => p.Key != GetIdentifier()).ToDictionary(p => p.Key, p => p.Value.Clone());
        foreach (var (name, value) in scoped)
            flattened[name] = JsonSerializer.SerializeToElement(value, Json);

        var kind = flattened.TryGetValue("generationType", out var generationType)
            ? generationType.GetString() : null;
        if (kind is "music" or "sfx" || options.Model.Contains("music", StringComparison.OrdinalIgnoreCase)
            || options.Model.Contains("sound-effects", StringComparison.OrdinalIgnoreCase))
        {
            flattened["generationType"] = JsonSerializer.SerializeToElement(kind
                ?? (options.Model.Contains("music", StringComparison.OrdinalIgnoreCase) ? "music" : "sfx"));
            var generated = await GenerateAudioAsync(new SpeechRequest
            {
                Model = options.Model, Text = options.Input,
                ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(flattened, Json) }
            }, cancellationToken);
            return generated.ToOpenAISpeechAudio();
        }

        options = JsonSerializer.Deserialize<AudioSpeechRequest>(JsonSerializer.Serialize(options, Json), Json)!;
        options.AdditionalProperties = flattened;
        using var client = CreateClient();
        return await client.OpenAICompatibleSpeechRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Completed-audio fallback, without assuming provider-specific SSE support.
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var providerOptions = GetMediaOptions(request.ProviderOptions);
        var kind = ReadOptionString(providerOptions, "generationType");
        if (kind is "music" or "sfx" || request.Model.Contains("music", StringComparison.OrdinalIgnoreCase)
            || request.Model.Contains("sound-effects", StringComparison.OrdinalIgnoreCase))
        {
            providerOptions["generationType"] = kind ?? (request.Model.Contains("music", StringComparison.OrdinalIgnoreCase) ? "music" : "sfx");
            var generation = JsonSerializer.Deserialize<SpeechRequest>(JsonSerializer.Serialize(request, Json), Json)!;
            generation.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(providerOptions, Json) };
            return await GenerateAudioAsync(generation, cancellationToken);
        }

        var format = request.OutputFormat ?? "mp3";
        var options = new AudioSpeechRequest
        {
            Model = request.Model, Input = request.Text, Voice = request.Voice, Speed = request.Speed,
            ResponseFormat = format, Instructions = request.Instructions,
            AdditionalProperties = providerOptions.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value, Json))
        };
        var (audio, mimeType) = await OpenAISpeechRequestAsync(options, cancellationToken);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(audio), MimeType = mimeType, Format = format },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Request = new SpeechRequestItem { Body = options },
            Response = new ResponseData { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        return await client.OpenAICompatibleTranscriptionRequestAsync(options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await foreach (var part in client.OpenAICompatibleTranscriptionStreamingAsync(options, cancellationToken: cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = CreateClient();
        await foreach (var part in client.OpenAICompatibleVercelTranscriptionStreamingAsync(request, GetIdentifier(), cancellationToken: cancellationToken))
            yield return part;
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        var data = request.Audio is JsonElement element ? element.GetString() : request.Audio.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) data = data[(data.IndexOf(',') + 1)..];
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        var options = GetMediaOptions(request.ProviderOptions);
        options["model"] = request.Model;
        options["response_format"] = "verbose_json";
        using var multipart = new MultipartFormDataContent();
        var audio = new StreamContent(stream);
        audio.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        multipart.Add(audio, "file", "audio");
        foreach (var (name, value) in options)
        {
            if (name == "file") continue;
            var elementValue = JsonSerializer.SerializeToElement(value, Json);
            if (elementValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in elementValue.EnumerateArray())
                    multipart.Add(new StringContent(item.ValueKind == JsonValueKind.String
                        ? item.GetString()! : item.GetRawText()), name + "[]");
            }
            else if (elementValue.ValueKind != JsonValueKind.Null)
                multipart.Add(new StringContent(elementValue.ValueKind == JsonValueKind.String
                    ? elementValue.GetString()! : elementValue.GetRawText()), name);
        }

        using var client = CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions") { Content = multipart };
        using var httpResponse = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"AppNZ transcription failed ({(int)httpResponse.StatusCode}): {body}", null, httpResponse.StatusCode);

        using var document = JsonDocument.Parse(body);
        var raw = document.RootElement.Clone();
        var response = raw.Deserialize<OpenAITranscriptionVerboseResponse>(Json)!;
        return new TranscriptionResponse
        {
            Text = response.Text, Language = response.Language, DurationInSeconds = (float)response.Duration,
            Segments = response.Segments?.Select(s => new TranscriptionSegment
            {
                Text = s.Text, StartSecond = (float)s.Start, EndSecond = (float)s.End
            }) ?? [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(raw),
            Response = new ResponseData { ModelId = request.Model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow, Body = raw }
        };
    }
}
