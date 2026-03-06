using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NimbleWay;

public partial class NimbleWayProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var query = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "NimbleWay requires at least one text message.".ToErrorUIPart();
            yield break;
        }

        var passthrough = GetRawProviderPassthroughFromChatRequest(chatRequest);
        var result = await ExecuteNimbleWayAsync(chatRequest.Model, query, passthrough, cancellationToken);

        foreach (var source in result.Results.Where(r => !string.IsNullOrWhiteSpace(r.Url)))
        {
            yield return new SourceUIPart
            {
                SourceId = source.Url!,
                Url = source.Url!,
                Title = source.Title
            };
        }

        var answer = BuildPrimaryAnswerText(result);
        var streamId = Guid.NewGuid().ToString("n");

        if (!string.IsNullOrWhiteSpace(answer))
        {
            yield return streamId.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = streamId,
                Delta = answer
            };
            yield return streamId.ToTextEndUIMessageStreamPart();
        }

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: 0,
            inputTokens: 0,
            totalTokens: 0,
            temperature: chatRequest.Temperature,
            extraMetadata: new Dictionary<string, object>
            {
                ["requestId"] = result.RequestId ?? string.Empty,
                ["resultCount"] = result.Results.Count
            });
    }
}
