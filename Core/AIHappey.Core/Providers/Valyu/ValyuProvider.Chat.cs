using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Valyu;

public partial class ValyuProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = chatRequest.Model;
        if (IsAnswerModel(model))
        {
            await foreach (var part in StreamAnswerUiAsync(chatRequest, cancellationToken))
                yield return part;

            yield break;
        }

        if (IsDeepResearchModel(model))
        {
            await foreach (var part in StreamDeepResearchUiAsync(chatRequest, cancellationToken))
                yield return part;

            yield break;
        }

        yield return $"Valyu model '{model}' is not supported.".ToErrorUIPart();
        yield return "error".ToFinishUIPart(
            model: model ?? string.Empty,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest?.Temperature);
    }

    private async IAsyncEnumerable<UIMessagePart> StreamAnswerUiAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "Valyu answer requires at least one text message.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var searchType = ResolveAnswerSearchType(chatRequest.Model);
        var passthrough = GetRawProviderPassthroughFromChatRequest(chatRequest);
        var streamId = Guid.NewGuid().ToString("n");
        var started = false;
        Dictionary<string, object?>? lastMetadata = null;

        await foreach (var evt in StreamAnswerEventsAsync(query, searchType, passthrough, cancellationToken))
        {
            foreach (var source in evt.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url))
                    continue;

                yield return new SourceUIPart
                {
                    SourceId = source.Url!,
                    Url = source.Url!,
                    Title = source.Title
                };
            }

            if (!string.IsNullOrWhiteSpace(evt.Delta))
            {
                if (!started)
                {
                    yield return streamId.ToTextStartUIMessageStreamPart();
                    started = true;
                }

                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = streamId,
                    Delta = evt.Delta
                };
            }

            if (evt.Metadata is not null)
                lastMetadata = evt.Metadata;
        }

        if (started)
            yield return streamId.ToTextEndUIMessageStreamPart();

        var usage = ExtractUsageFromMetadata(lastMetadata);
        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: usage.OutputTokens,
            inputTokens: usage.InputTokens,
            totalTokens: usage.TotalTokens,
            temperature: chatRequest.Temperature,
            extraMetadata: lastMetadata?.ToDictionary(k => k.Key, v => (object)v.Value!));
    }

    private async IAsyncEnumerable<UIMessagePart> StreamDeepResearchUiAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var query = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "Valyu deep research requires at least one text message.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var mode = ResolveDeepResearchMode(chatRequest.Model);
        var passthrough = GetRawProviderPassthroughFromChatRequest(chatRequest);
        var result = await ExecuteDeepResearchAsync(query, mode, passthrough, downloadArtifacts: true, cancellationToken);

        foreach (var source in result.Sources.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
        {
            yield return new SourceUIPart
            {
                SourceId = source.Url!,
                Url = source.Url!,
                Title = source.Title
            };
        }

        foreach (var file in result.Files)
            yield return file;

        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            var streamId = Guid.NewGuid().ToString("n");
            yield return streamId.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = result.Text };
            yield return streamId.ToTextEndUIMessageStreamPart();
        }

        if (!result.IsSuccess)
            yield return (result.Error ?? "Valyu deep research failed.").ToErrorUIPart();

        yield return (result.IsSuccess ? "stop" : "error").ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest.Temperature,
            extraMetadata: result.Metadata?.ToDictionary(k => k.Key, v => (object)v.Value!));
    }
}
