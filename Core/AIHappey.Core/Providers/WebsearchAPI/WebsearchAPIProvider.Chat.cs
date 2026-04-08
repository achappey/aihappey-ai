using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WebsearchAPI;

public partial class WebsearchAPIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var query = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "WebsearchAPI requires at least one text message.".ToErrorUIPart();
            yield break;
        }

        var passthrough = GetRawProviderPassthroughFromChatRequest(chatRequest);
        var result = await ExecuteAiSearchAsync(query, passthrough, cancellationToken);

        foreach (var source in result.Organic.Where(o => !string.IsNullOrWhiteSpace(o.Url)))
        {
            yield return new SourceUIPart
            {
                SourceId = source.Url!,
                Url = source.Url!,
                Title = source.Title,
            };
        }

        var text = BuildPrimaryAnswerText(result);
        var streamId = Guid.NewGuid().ToString("n");

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return streamId.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = streamId,
                Delta = text
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
                ["responseTime"] = result.ResponseTime
            });
    }
}
