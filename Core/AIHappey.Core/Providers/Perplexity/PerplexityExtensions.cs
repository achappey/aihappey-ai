
using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.Perplexity.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityExtensions
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
            foreach (var filePart in msg.Parts.OfType<AIHappey.Common.Model.FileUIPart>())
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
            if (msg.Role == AIHappey.Common.Model.Role.system && systemRole == null)
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
                if (imageContentBlock.MimeType is not null
                                   && imageContentBlock.MimeType
                                       .StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                   && imageContentBlock.Data is not null
                                   && imageContentBlock.Data
                                       .StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    contentParts.Add(new PerplexityImageUrlContent
                    {
                        Url = new PerplexityUrlItem
                        {
                            Url = imageContentBlock.Data
                        }
                    });
                }
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

    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static PerplexityChatRequest ToChatRequest(
        this ChatRequest chatRequest,
        IEnumerable<PerplexityMessage> messages,
        string? systemRole = null)
    {
        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        static string? F(DateTime? dt) =>
            dt.HasValue ? dt.Value.ToString("M/d/yyyy", CultureInfo.InvariantCulture) : null;
        var metadata = chatRequest.GetProviderMetadata<PerplexityProviderMetadata>(nameof(Perplexity).ToLowerInvariant());
        var px = metadata;

        return new PerplexityChatRequest
        {
            Model = chatRequest.Model,
            Messages = messages,
            Temperature = chatRequest.Temperature,

            // Existing
            SearchMode = px?.SearchMode,
            ReturnImages = px?.ReturnImages,
            ReturnRelatedQuestions = px?.ReturnRelatedQuestions,
            WebSearchOptions = px?.WebSearchOptions,
            EnableSearchClassifier = px?.EnableSearchClassifier,
            MediaResponse = px?.MediaResponse,

            // NEW: Date filters (formatted) + recency
            SearchRecencyFilter = px?.SearchRecencyFilter,                 // string (e.g., "week", "day")
            SearchAfterDateFilter = F(px?.SearchAfterDateFilter),          // "M/d/yyyy"
            SearchBeforeDateFilter = F(px?.SearchBeforeDateFilter),        // "M/d/yyyy"
            LastUpdatedAfterFilter = F(px?.LastUpdatedAfterFilter),        // "M/d/yyyy"
            LastUpdatedBeforeFilter = F(px?.LastUpdatedBeforeFilter),      // "M/d/yyyy"
        };
    }

    public static PerplexityMessageContent ToPerplexityMessageContent(this string text) =>
      new()
      {
          Text = text
      };


    public static PerplexityChatRequest ToChatRequest(this CreateMessageRequestParams chatRequest,
           IEnumerable<PerplexityMessage> messages,
           string? systemRole = null)
    {
        string? searchMode = null;
        if (chatRequest.Metadata is JsonElement el &&
            el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("perplexity", out var perplexityObj) &&
            perplexityObj.ValueKind == JsonValueKind.Object &&
            perplexityObj.TryGetProperty("search_mode", out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            searchMode = prop.GetString();
        }

        string? searchContextSize = null;

        if (chatRequest.Metadata is JsonElement el1 &&
            el1.ValueKind == JsonValueKind.Object &&
            el1.TryGetProperty("perplexity", out var perplexityObj1) &&
            perplexityObj1.ValueKind == JsonValueKind.Object &&
            perplexityObj1.TryGetProperty("web_search_options", out var webSearchOptionsObj) &&
            webSearchOptionsObj.ValueKind == JsonValueKind.Object &&
            webSearchOptionsObj.TryGetProperty("search_context_size", out var sizeProp) &&
            sizeProp.ValueKind == JsonValueKind.String)
        {
            searchContextSize = sizeProp.GetString();
        }


        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage()
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        return new PerplexityChatRequest()
        {
            Model = chatRequest.GetModel() ?? "sonar",
            Messages = messages,
            SearchMode = searchMode,
            WebSearchOptions = new()
            {
                SearchContextSize = Enum.Parse<SearchContextSize>(searchContextSize ?? "medium")
            },
            Temperature = chatRequest.Temperature,
        };
    }


    public static PerplexityChatRequest ToChatRequest(this string model,
           IEnumerable<PerplexityMessage> messages,
           string? systemRole = null,
           string chatReasoningEffortLevel = "medium",
           double? temperature = 1,
           JsonElement? metadata = null,
           PerplexityProviderMetadata? perplexityProviderMetadata = null)
    {
        string? searchMode = perplexityProviderMetadata?.SearchMode;
        if (metadata is JsonElement el &&
            el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("search_mode", out var prop) &&
            prop.GetString() == "academic")
        {
            searchMode = prop.GetString();
            // Found in JsonElement
        }

        if (!string.IsNullOrEmpty(systemRole))
        {
            messages = [new PerplexityMessage()
            {
                Role = "system",
                Content = [systemRole.ToPerplexityMessageContent()],
            }, .. messages];
        }

        return new PerplexityChatRequest()
        {
            Model = model,
            Messages = messages,
            SearchMode = searchMode,
            ReturnImages = perplexityProviderMetadata?.ReturnImages,
            ReturnRelatedQuestions = perplexityProviderMetadata?.ReturnRelatedQuestions,
            WebSearchOptions = perplexityProviderMetadata?.WebSearchOptions ?? new()
            {
                SearchContextSize = Enum.Parse<SearchContextSize>(chatReasoningEffortLevel)
            },
            Temperature = temperature,
        };
    }

    public static async Task<PerplexityChatResponse?> ChatCompletion(this HttpClient httpClient,
         PerplexityChatRequest request, CancellationToken cancellationToken = default)
    {
        // Ensure streaming is off for non-streaming scenario
        request.Stream = false;

        // Serialize the request
        var requestJson = JsonSerializer.Serialize(request, options);

        // Build the HTTP request
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // Send the request
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(responseContent ?? response.ReasonPhrase);
        }

        return JsonSerializer.Deserialize<PerplexityChatResponse>(responseContent, options);
    }

    /// <summary>
    /// Streaming chat completion request to Perplexity.
    /// This method returns partial content as it's received.
    /// </summary>
    public static async IAsyncEnumerable<PerplexityChatResponse> ChatCompletionStreaming(
        this HttpClient httpClient,
        PerplexityChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Enable streaming on the request
        request.Stream = true;

        // Serialize the request
        var requestJson = JsonSerializer.Serialize(request, options);

        // Build the HTTP request
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        //    response.EnsureSuccessStatusCode();

        // The exact streaming format from Perplexity might vary, but let's assume line-delimited JSON or SSE-like format.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            // Read data line-by-line or chunk-by-chunk
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var content = line?.StartsWith("data: ") == true ? line["data: ".Length..] : line;
            // Return the raw chunk. The caller can parse partial JSON or text deltas as needed.
            yield return JsonSerializer.Deserialize<PerplexityChatResponse>(content!)!;
        }
    }
}
