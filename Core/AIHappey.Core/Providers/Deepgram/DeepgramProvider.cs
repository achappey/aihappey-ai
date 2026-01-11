using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Deepgram;

public sealed partial class DeepgramProvider
    : IModelProvider
{
     private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public DeepgramProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.deepgram.com/");
    }

    public string GetIdentifier() => "deepgram";

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Deepgram API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token",  key);
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
           [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("aura") == true)
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
            yield return p;
    }
    
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Deepgram docs enumerate available TTS + STT models; we hard-code as requested.
        // NOTE: `SpeechTools` will resolve providers by model id, so these must match user-facing IDs.
        const string owner = "Deepgram";

        string[] speechModels =
        [
            "aura-asteria-en",
            "aura-luna-en",
            "aura-stella-en",
            "aura-athena-en",
            "aura-hera-en",
            "aura-orion-en",
            "aura-arcas-en",
            "aura-perseus-en",
            "aura-angus-en",
            "aura-orpheus-en",
            "aura-helios-en",
            "aura-zeus-en",
            "aura-2-amalthea-en",
            "aura-2-andromeda-en",
            "aura-2-apollo-en",
            "aura-2-arcas-en",
            "aura-2-aries-en",
            "aura-2-asteria-en",
            "aura-2-athena-en",
            "aura-2-atlas-en",
            "aura-2-aurora-en",
            "aura-2-callista-en",
            "aura-2-cordelia-en",
            "aura-2-cora-en",
            "aura-2-delia-en",
            "aura-2-draco-en",
            "aura-2-electra-en",
            "aura-2-harmonia-en",
            "aura-2-helena-en",
            "aura-2-hera-en",
            "aura-2-hermes-en",
            "aura-2-hyperion-en",
            "aura-2-iris-en",
            "aura-2-janus-en",
            "aura-2-juno-en",
            "aura-2-jupiter-en",
            "aura-2-luna-en",
            "aura-2-mars-en",
            "aura-2-minerva-en",
            "aura-2-neptune-en",
            "aura-2-odysseus-en",
            "aura-2-ophelia-en",
            "aura-2-orion-en",
            "aura-2-orpheus-en",
            "aura-2-pandora-en",
            "aura-2-phoebe-en",
            "aura-2-pluto-en",
            "aura-2-saturn-en",
            "aura-2-selene-en",
            "aura-2-thalia-en",
            "aura-2-theia-en",
            "aura-2-vesta-en",
            "aura-2-zeus-en",
            "aura-2-sirio-es",
            "aura-2-nestor-es",
            "aura-2-carina-es",
            "aura-2-celeste-es",
            "aura-2-alvaro-es",
            "aura-2-diana-es",
            "aura-2-aquila-es",
            "aura-2-selena-es",
            "aura-2-estrella-es",
            "aura-2-javier-es",
        ];

        // Pre-recorded STT models (POST /v1/listen)
        string[] transcriptionModels =
        [
            "nova-3",
            "nova-3-general",
            "nova-3-medical",
            "nova-2",
            "nova-2-general",
            "nova-2-meeting",
            "nova-2-finance",
            "nova-2-conversationalai",
            "nova-2-voicemail",
            "nova-2-video",
            "nova-2-medical",
            "nova-2-drivethru",
            "nova-2-automotive",
            "nova",
            "nova-general",
            "nova-phonecall",
            "nova-medical",
            "enhanced",
            "enhanced-general",
            "enhanced-meeting",
            "enhanced-phonecall",
            "enhanced-finance",
            "base",
            "meeting",
            "phonecall",
            "finance",
            "conversationalai",
            "voicemail",
            "video",
        ];

        var speech = speechModels.Select(id => new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = id,
            OwnedBy = owner,
            Type = "speech",
        });

        var transcriptions = transcriptionModels.Select(id => new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = id,
            OwnedBy = owner,
            Type = "transcription",
        });

        IEnumerable<Model> res = speech.Concat(transcriptions);

        return Task.FromResult(res);
    }

}

