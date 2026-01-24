using AIHappey.Vercel.Models;
using Anthropic.SDK.Messaging;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{

    public static IEnumerable<UIMessagePart> ToStreamingResponseUpdate(this MessageResponse update, string? streamId)
    {
        if (update.Type == "error")
        {
            yield return new ErrorUIPart()
            {
            };
        }

        if (update.Delta?.Citation != null)
        {
            if (!string.IsNullOrEmpty(update.Delta.Citation.CitedText) && !string.IsNullOrEmpty(streamId))
            {
                if (!string.IsNullOrEmpty(update.Delta.Citation.Url))
                    yield return update.Delta.Citation.ToSourceUIPart();
            }
        }
    }

    public static SourceUIPart ToSourceUIPart(this CitationResult citationResult)
        => new()
        {
            Url = citationResult.Url!,
            SourceId = citationResult.Url,
            Title = citationResult.Title
        };


    public static SourceUIPart ToSourceUIPart(this WebSearchResultContent userLocation)
        => new()
        {
            Url = userLocation.Url!,
            SourceId = userLocation.Url,
            Title = userLocation.Title
        };



}
