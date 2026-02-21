using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.Perplexity.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityMappingExtensions
{

    public static SourceUIPart ToSourceUIPart(this PerplexityVideoResult perplexityVideoResult) =>
        new()
        {
            Url = perplexityVideoResult.Url,
            Title = new Uri(perplexityVideoResult.Url).Host,
            SourceId = perplexityVideoResult.Url,
        };

    public static SourceUIPart ToSourceUIPart(this PerplexitySearchResult perplexitySearchResult) =>
        new()
        {
            Url = perplexitySearchResult.Url,
            Title = perplexitySearchResult.Title,
            SourceId = perplexitySearchResult.Url,
        };

    public static readonly HashSet<string> SupportedFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",                  // .pdf
        "application/msword",               // .doc
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "text/plain",                       // .txt
        "application/rtf",                  // .rtf
        "text/rtf"                          // fallback / alt-type sometimes used
    };

    public static (List<PerplexityMessage> Messages, string? SystemRole)
        ToPerplexityMessages(this List<UIMessage> uiMessages)
    {
        var messages = new List<PerplexityMessage>();
        string? systemRole = null;

        foreach (var msg in uiMessages)
        {
            // Collect all text/image parts for this message
            var contentParts = new List<IPerplexityMessageContent>();

            // Text parts
            foreach (var textPart in msg.Parts.OfType<TextUIPart>())
            {
                if (!string.IsNullOrWhiteSpace(textPart.Text))
                {
                    contentParts.Add(textPart.Text.ToPerplexityMessageContent());
                }
            }

            // Image file parts (base64 data URIs)
            foreach (var filePart in msg.Parts.OfType<FileUIPart>())
            {
                if (filePart.MediaType is not null
                    && filePart.MediaType
                        .StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && filePart.Url is not null
                    && filePart.Url
                        .StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    // Use the full data URI as-is, since that's what Perplexity expects
                    contentParts.Add(new PerplexityImageUrlContent
                    {
                        Url = new PerplexityUrlItem
                        {
                            Url = filePart.Url
                        }
                    });
                }

                if (filePart.MediaType is not null
                    && SupportedFileTypes.Contains(filePart.MediaType)
                    && filePart.Url is not null
                    && !filePart.Url
                        .StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    contentParts.Add(new PerplexityFileContent
                    {
                        Url = new PerplexityUrlItem
                        {
                            Url = filePart?.GetRawBase64String()!
                        }
                    });
                }
            }

            // Skip empty content
            if (contentParts.Count == 0) continue;

            // System role handling: only use the first system message as a param, not a message
            if (msg.Role == Vercel.Models.Role.system && systemRole == null)
            {
                // If there are text parts, take the first as system prompt
                var firstText = contentParts
                    .OfType<PerplexityMessageContent>()
                    .FirstOrDefault();

                if (firstText != null)
                {
                    systemRole = firstText.Text;
                }
                continue;
            }

            messages.Add(new PerplexityMessage
            {
                Role = msg.Role.ToString(),
                Content = contentParts
            });
        }
        return (messages, systemRole);
    }

    public static IEnumerable<PerplexityMessage> ToPerplexityMessages(this IList<SamplingMessage> uiMessages)
        => uiMessages.Select(a => a.ToPerplexityMessage());

    public static PerplexityMessage ToPerplexityMessage(this SamplingMessage msg)
    {
        var contentParts = new List<IPerplexityMessageContent>();
        foreach (var content in msg.Content)
        {
            if (content is TextContentBlock textContentBlock)
            {
                contentParts.Add(textContentBlock.Text.ToPerplexityMessageContent());
            }
            else if (content is ImageContentBlock imageContentBlock)
            {
                contentParts.Add(new PerplexityImageUrlContent
                {
                    Url = new PerplexityUrlItem
                    {
                        Url = Convert.ToBase64String(imageContentBlock.Data.ToArray()).ToDataUrl(imageContentBlock.MimeType)
                    }
                });
            }
            else if (content is EmbeddedResourceBlock embeddedResourceBlock)
            {
                if (embeddedResourceBlock.Resource.MimeType is not null
                               && SupportedFileTypes.Contains(embeddedResourceBlock.Resource.MimeType)
                               && embeddedResourceBlock.Resource.Uri is not null
                               && embeddedResourceBlock.Resource.Uri
                                   .StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    contentParts.Add(new PerplexityFileContent
                    {
                        Url = new PerplexityUrlItem
                        {
                            Url = embeddedResourceBlock.Resource.Uri
                        }
                    });
                }
            }
        }

        return new PerplexityMessage
        {
            Role = msg.Role.ToString().ToLowerInvariant(),
            Content = contentParts
        };
    }


    public static PerplexityMessageContent ToPerplexityMessageContent(this string text) =>
      new()
      {
          Text = text
      };
}
