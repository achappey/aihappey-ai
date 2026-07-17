using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Core.Models;
using AIHappey.Common.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.TextSynth;

public partial class TextSynthProvider : IModelProvider
{
    private const string SpeechBaseModel = "parler_tts_large";
    private const string TranscriptionBaseModel = "whisper_large_v3";
    private const string ImageBaseModel = "stable_diffusion";

    private static readonly string[] TextSynthSpeechVoices =
    [
        "Will", "Eric", "Laura", "Alisa", "Patrick", "Rose", "Jerry", "Jordan", "Lauren", "Jenna",
        "Karen", "Rick", "Bill", "James", "Yann", "Emily", "Anna", "Jon", "Brenda", "Barbara"
    ];

    private static readonly HashSet<string> TextSynthSpeechVoicesSet =
        new(TextSynthSpeechVoices, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions TextSynthJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public TextSynthProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.textsynth.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(TextSynth)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: $"v1/engines/{options.Model}/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: $"v1/engines/{options.Model}/completions",
                    cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
           => await ListModelsInternal(cancellationToken);

    public string GetIdentifier() => nameof(TextSynth).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => TextSynthTranscriptionRequest(imageRequest, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => TextSynthSpeechRequest(imageRequest, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => TextSynthImageRequest(request, cancellationToken);

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private string ExtractProviderLocalModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var trimmed = modelId.Trim();
        var providerPrefix = GetIdentifier() + "/";

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[providerPrefix.Length..]
            : trimmed;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(metadata, name, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }

        return null;
    }

    private static int? TryGetInt(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(metadata, name, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
                return i;

            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out i))
                return i;
        }

        return null;
    }

    private static double? TryGetDouble(JsonElement metadata, params string[] names)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(metadata, name, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
                return d;

            if (el.ValueKind == JsonValueKind.String
                && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
            {
                return d;
            }
        }

        return null;
    }

    private static float? TryGetFloat(JsonElement metadata, params string[] names)
        => TryGetDouble(metadata, names) is { } d ? (float)d : null;

    private static byte[] DecodeBase64Payload(string value)
    {
        var payload = value.RemoveDataUrlPrefix();

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Invalid base64 payload.", nameof(value), ex);
        }
    }

    private static string GetAudioFileExtension(string? mediaType)
    {
        return mediaType?.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "video/mp4" => ".mp4",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/opus" => ".opus",
            "audio/ogg" => ".ogg",
            _ => ".bin"
        };
    }

    private (string BaseModel, string? Voice) ResolveSpeechModelAndVoice(string model)
    {
        var local = ExtractProviderLocalModelId(model);
        if (string.IsNullOrWhiteSpace(local))
            throw new ArgumentException("Model is required.", nameof(model));

        if (string.Equals(local, SpeechBaseModel, StringComparison.OrdinalIgnoreCase))
            return (SpeechBaseModel, null);

        var prefix = SpeechBaseModel + "/";
        if (!local.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"TextSynth speech model '{model}' is not supported.");

        var voice = local[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("TextSynth speech model must include a voice in the form 'parler_tts_large/{voice}'.", nameof(model));

        if (!TextSynthSpeechVoicesSet.Contains(voice))
            throw new NotSupportedException($"TextSynth speech voice '{voice}' is not supported.");

        var normalizedVoice = TextSynthSpeechVoices.First(v => string.Equals(v, voice, StringComparison.OrdinalIgnoreCase));
        return (SpeechBaseModel, normalizedVoice);
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
