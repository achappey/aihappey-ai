using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using Microsoft.KernelMemory;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.KernelMemory;

public partial class KernelMemoryProvider(IApiKeyResolver keyResolver,
    IKernelMemory kernelMemory, OpenAIProvider openAIProvider) : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver = keyResolver;

    private string GetIndex()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(KernelMemory)} API key.");

        return key;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(KernelMemory).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        GetIndex();

        return KernelMemoryModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = GetIndex();

        var userMessage = chatRequest.Messages.LastOrDefault(a => a.Role == Vercel.Models.Role.user);
        var text = string.Join("\n\n", userMessage?.Parts.OfType<TextUIPart>().Select(a => a.Text) ?? []);
        string? messageId = null;
        List<TokenUsage> tokenUsage = [];

        await foreach (var p in kernelMemory.AskStreamingAsync(text, index, minRelevance: 0,
            options: new SearchOptions()
            {
                Stream = true
            },
            cancellationToken: cancellationToken))
        {
            Console.WriteLine(JsonSerializer.Serialize(p));

            switch (p.StreamState)
            {
                case StreamStates.Reset:
                    if (messageId != null)
                    {
                        yield return new TextEndUIMessageStreamPart()
                        {
                            Id = messageId
                        };
                    }

                    chatRequest.Model = "openai/gpt-5.2";

                    await foreach (var z in openAIProvider.StreamAsync(chatRequest, cancellationToken))
                        yield return z;

                    yield break;
                case StreamStates.Append:

                    if (messageId == null)
                    {
                        messageId = Guid.NewGuid().ToString();

                        yield return new TextStartUIMessageStreamPart()
                        {
                            Id = messageId
                        };
                    }

                    yield return new TextDeltaUIMessageStreamPart()
                    {
                        Delta = p.Result,
                        Id = messageId
                    };

                    foreach (var r in p.RelevantSources
                        .Where(a => !string.IsNullOrEmpty(a.SourceUrl)))
                    {
                        yield return new SourceUIPart()
                        {
                            SourceId = r.SourceUrl!,
                            Url = r.SourceUrl!
                        };
                    }

                    break;
                default:
                    break;
            }

            if (p.TokenUsage != null)
            {
                tokenUsage.AddRange(p.TokenUsage);
            }
        }

        if (messageId != null)
        {
            yield return new TextEndUIMessageStreamPart()
            {
                Id = messageId
            };
        }

        yield return "stop".ToFinishUIPart(
            chatRequest.Model,
            outputTokens: tokenUsage.Sum(z => z.ServiceTokensOut) ?? 0,
            inputTokens: tokenUsage.Sum(z => z.ServiceTokensIn) ?? 0,
            totalTokens: tokenUsage.Sum(z => z.ServiceTokensIn + z.ServiceTokensOut) ?? 0,
            temperature: chatRequest.Temperature
        );
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public static IReadOnlyList<Model> KernelMemoryModels =>
        [
            new() { Id = "kernelmemory/shared", Name = "Shared Memory", Type = "language" },
        ];

}
