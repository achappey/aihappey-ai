using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Common.Extensions;
using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(chatRequest.Model))
        {
            await foreach (var part in ExecuteNativeSearchUiStreamAsync(chatRequest, cancellationToken))
                yield return part;

            yield break;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "v1/chat",
            cancellationToken: cancellationToken))
            yield return update;
    }

    private Dictionary<string, object?>? GetRawProviderPassthroughFromChatRequest(ChatRequest request)
    {
        var raw = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        return JsonElementObjectToDictionary(raw);
    }

    private NinjaChatSearchRequest BuildNativeSearchRequest(ChatRequest request)
        => BuildNativeSearchRequest(
            query: BuildPromptFromUiMessages(request.Messages),
            passthrough: GetRawProviderPassthroughFromChatRequest(request));


    private ChatRequest BuildNativeSearchChatRequest(JsonElement request)
        => new()
        {
            Model = ExtractModelFromMessagesRequest(request) ?? NativeSearchModelId,
            Messages =
            [
                new UIMessage
                {
                    Role = Role.user,
                    Parts =
                    [
                        new TextUIPart
                        {
                            Text = BuildPromptFromMessagesRequest(request)
                        }
                    ]
                }
            ]
        };


    private async IAsyncEnumerable<UIMessagePart> ExecuteNativeSearchUiStreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var execution = await ExecuteNativeSearchAsync(BuildNativeSearchRequest(chatRequest), cancellationToken);
        var responseId = execution.Id;

        yield return new StepStartUIPart();

        foreach (var source in execution.Response.Sources)
        {
            var part = ToSourcePart(source);
            if (part is not null)
                yield return part;
        }

        if (execution.Response.Images.Count > 0)
        {
            yield return new DataUIPart
            {
                Type = "data-search-images",
                Data = execution.Response.Images
                    .Where(i => !string.IsNullOrWhiteSpace(i.Url))
                    .Select((image, index) => new
                    {
                        index,
                        image.Url,
                        image.Description
                    })
                    .ToArray()
            };
        }

        yield return new MessageMetadataUIPart
        {
            MessageMetadata = BuildNativeSearchMessageMetadata(execution.Response)
        };

        if (!string.IsNullOrWhiteSpace(execution.Text))
        {
            yield return responseId.ToTextStartUIMessageStreamPart();

            foreach (var chunk in ChunkText(execution.Text))
            {
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = responseId,
                    Delta = chunk
                };
            }

            yield return responseId.ToTextEndUIMessageStreamPart();
        }

        if (execution.Response.FollowUpQuestions.Count > 0)
        {
            yield return new DataUIPart
            {
                Type = "data-follow-up-questions",
                Data = execution.Response.FollowUpQuestions.ToArray()
            };
        }

        yield return new FinishUIPart
        {
            FinishReason = "stop",
            MessageMetadata = BuildNativeSearchMessageMetadata(execution.Response)
        };
    }


    private static IEnumerable<UIMessagePart> BuildNativeSearchUiParts(NinjaChatSearchExecutionResult execution)
    {
        var parts = new List<UIMessagePart>();

        parts.AddRange(execution.Response.Sources.Select(ToSourcePart).OfType<UIMessagePart>());

        if (execution.Response.Images.Count > 0)
        {
            parts.Add(new DataUIPart
            {
                Type = "data-search-images",
                Data = execution.Response.Images
            });
        }

        parts.Add(new TextUIPart
        {
            Text = execution.Text
        });

        if (execution.Response.FollowUpQuestions.Count > 0)
        {
            parts.Add(new DataUIPart
            {
                Type = "data-follow-up-questions",
                Data = execution.Response.FollowUpQuestions
            });
        }

        return parts;
    }

    private static SourceUIPart? ToSourcePart(NinjaChatSearchSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return null;

        Dictionary<string, object>? providerMetadata = null;
        if (!string.IsNullOrWhiteSpace(source.Content) || !string.IsNullOrWhiteSpace(source.PublishedDate))
        {
            providerMetadata = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(source.Content))
                providerMetadata["content"] = source.Content!;
            if (!string.IsNullOrWhiteSpace(source.PublishedDate))
                providerMetadata["published_date"] = source.PublishedDate!;
        }

        return new SourceUIPart
        {
            SourceId = source.Url,
            Url = source.Url,
            Title = string.IsNullOrWhiteSpace(source.Title) ? source.Url : source.Title,
            ProviderMetadata = providerMetadata?.ToProviderMetadata(nameof(NinjaChat).ToLowerInvariant())
        };
    }

}
