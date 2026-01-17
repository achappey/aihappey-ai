using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Providers.Telnyx;
using AIHappey.Common.Model.Responses;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider : IModelProvider
{
    public Task<string> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Chat with transcription models: treat "whisper*" and "distil-whisper/*" as STT.
        if (chatRequest.Model.Contains("whisper", StringComparison.OrdinalIgnoreCase)
            || chatRequest.Model.StartsWith("distil-whisper/", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<TelnyxProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = [];

        if (metadata?.GuidedJson is not null)
            payload["guided_json"] = metadata.GuidedJson;

        if (!string.IsNullOrWhiteSpace(metadata?.GuidedRegex))
            payload["guided_regex"] = metadata.GuidedRegex;

        if (metadata?.GuidedChoice?.Any() == true)
            payload["guided_choice"] = metadata.GuidedChoice.ToArray();

        if (metadata?.MinP is not null)
            payload["min_p"] = metadata.MinP;

        if (metadata?.UseBeamSearch is not null)
            payload["use_beam_search"] = metadata.UseBeamSearch;

        if (metadata?.BestOf is not null)
            payload["best_of"] = metadata.BestOf;

        if (metadata?.LengthPenalty is not null)
            payload["length_penalty"] = metadata.LengthPenalty;

        if (metadata?.EarlyStopping is not null)
            payload["early_stopping"] = metadata.EarlyStopping;

        if (metadata?.Logprobs is not null)
            payload["logprobs"] = metadata.Logprobs;

        if (metadata?.TopLogprobs is not null)
            payload["top_logprobs"] = metadata.TopLogprobs;

        if (metadata?.FrequencyPenalty is not null)
            payload["frequency_penalty"] = metadata.FrequencyPenalty;

        if (metadata?.PresencePenalty is not null)
            payload["presence_penalty"] = metadata.PresencePenalty;

        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            payload.Count > 0 ? payload : null,
            url: "ai/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

