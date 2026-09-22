using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.CanRouter;

public partial class CanRouterProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest { Model = request.Model, Prompt = request.Prompt, N = request.N, Size = request.Size }, cancellationToken);
#pragma warning disable CS0618
        var images = response.Data?.Select(item => !string.IsNullOrWhiteSpace(item.B64Json) ? item.B64Json!.ToDataUrl("image/png") : item.Url).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToList() ?? [];
#pragma warning restore CS0618
        return new ImageResponse { Images = images, Warnings = [], ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(), Response = new() { Timestamp = DateTimeOffset.FromUnixTimeSeconds(response.Created).UtcDateTime, ModelId = request.Model.ToModelId(GetIdentifier()) } };
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageGenerationRequestAsync(options, "v1/images/generations", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(options, "v1/images/generations", cancellationToken)) yield return item;
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleImageEditRequestAsync(options, "v1/images/edits", cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var item in _client.OpenAICompatibleImageEditNonStreamingAsStreamAsync(options, "v1/images/edits", cancellationToken)) yield return item;
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _client.OpenAICompatibleSpeechRequestAsync(options, "v1/audio/speech", cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _client.OpenAICompatibleTranscriptionRequestAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return _client.OpenAICompatibleTranscriptionStreamingAsync(options, "v1/audio/transcriptions", cancellationToken);
    }

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = string.IsNullOrWhiteSpace(request.OutputFormat) ? "mp3" : request.OutputFormat;
        var (audio, mimeType) = await OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = request.Model,
            Input = request.Text,
            Voice = request.Voice ?? "alloy",
            ResponseFormat = format,
            Instructions = request.Instructions,
            Speed = request.Speed
        }, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse { Base64 = Convert.ToBase64String(audio), MimeType = mimeType, Format = format },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.MediaType);
        form.Add(file, "file", "audio");
        form.Add(new StringContent(request.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"CanRouter transcription request failed ({(int)response.StatusCode}): {raw}");
        using var document = System.Text.Json.JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
            Language = root.TryGetProperty("language", out var language) ? language.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.TryGetSingle(out var seconds) ? seconds : null,
            Segments = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = "multipart/form-data" },
            Response = new ResponseData { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()), Body = root }
        };
    }
}
