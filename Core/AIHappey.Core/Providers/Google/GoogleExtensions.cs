using AIHappey.Core.AI;
using AIHappey.Common.Model;
using Mscc.GenerativeAI;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Google;

public static class GoogleExtensions
{
     public static Part? ToImagePart(
        this ImageFile imageContentBlock) =>
        new()
        {
            InlineData = new()
            {
                MimeType = imageContentBlock.MediaType,
                Data = imageContentBlock.Data
            }
        };


    public static ImageAspectRatio? ToImageAspectRatio(this string? value) =>
      value switch
      {
          "1:1" => ImageAspectRatio.Ratio1x1,
          "9:16" => ImageAspectRatio.Ratio9x16,
          "16:9" => ImageAspectRatio.Ratio16x9,
          "4:3" => ImageAspectRatio.Ratio4x3,
          "3:4" => ImageAspectRatio.Ratio3x4,
          "2:3" => ImageAspectRatio.Ratio2x3,
          "3:2" => ImageAspectRatio.Ratio3x2,
          "21:9" => ImageAspectRatio.Ratio21x9,
          _ => null
      };

    public static FileUIPart ToFileUIPart(
            this InlineData data) => new()
            {
                Url = data.ToDataUrl(),
                MediaType = data.MimeType
            };

    public static string ToDataUrl(
            this InlineData data) => data.Data.ToDataUrl(data.MimeType);

    public static ChatSession ToChatSession(
        this GoogleAI googleAI,
                GenerationConfig generationConfig,
                   string model,
                   string systemInstructions,
                   List<ContentResponse> history)
    {
        Content? systemInstruction = !string.IsNullOrEmpty(systemInstructions)
            ? new(systemInstructions, "model") : null;

        var generativeModel = googleAI.GenerativeModel(model,
            systemInstruction: systemInstruction
        );

        return generativeModel.StartChat(history, generationConfig);
    }

    public static bool IsValid(this Interval interval) =>
        interval?.StartTime.HasValue == true
        && interval?.EndTime.HasValue == true;

    public static string? ToStopReason(this
        FinishReason? update)
            => update switch
            {
                FinishReason.Stop => "endTurn",
                FinishReason.MaxTokens => "maxTokens",
                _ => update.HasValue ? Enum.GetName(update.Value) : null,
            };

    public static SourceUIPart ToSourceUIPart(this
            CitationSource citation) => new()
            {
                Url = citation.Uri ?? string.Empty,
                Title = citation.Title,
                SourceId = citation.Uri ?? string.Empty,
                ProviderMetadata = new Dictionary<string, object>()
                            {
                            {
                                "PublicationDate",
                                    citation.PublicationDate
                                    ?? DateTimeOffset.MinValue
                            },
                            { "License", citation.License ?? string.Empty },
                            { "StartIndex", citation.StartIndex! },
                            { "EndIndex", citation.EndIndex! }
                            }
            };


    public static IEnumerable<UIMessagePart> ToStreamingResponseUpdate(this
        GenerateContentResponse update)
    {
        foreach (var candidate in update.Candidates ?? []) // Replace CandidateType with actual type
        {
            var citations = candidate.CitationMetadata?.Citations;
            if (citations != null)
            {
                foreach (var citation in citations)
                {
                    yield return citation.ToSourceUIPart();
                }
            }

            var sourceParts = candidate.GroundingMetadata?.ToSourceUIParts(update.ResponseId!) ?? [];

            foreach (var citation in sourceParts)
            {
                yield return citation;
            }
        }

    }

    public static string Identifier() => nameof(Google).ToLowerInvariant();

    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
        => new()
        { { Identifier(), metadata } };

    public static IEnumerable<SourceUIPart> ToSourceUIParts(this GroundingMetadata groundingMetadata, string sourceId)
        => groundingMetadata?.GroundingChunks?
                .Select(t => t.Web)
                .OfType<WebChunk>()
                .Where(a => !string.IsNullOrEmpty(a.Uri))
                .Select(a => a.ToSourceUIPart(sourceId)) ?? [];

    public static SourceUIPart ToSourceUIPart(this WebChunk webChunk, string sourceId)
        => new()
        {
            Url = webChunk.Uri!,
            Title = webChunk.Title,
            SourceId = sourceId
        };
}
