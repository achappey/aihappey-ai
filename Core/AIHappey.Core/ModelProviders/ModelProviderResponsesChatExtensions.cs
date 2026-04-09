using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Messages.Mapping;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesChatExtensions
{
    public static void SetDefaultResponseProperties(
        this IModelProvider modelProvider, ResponseRequest responseRequest)
    {
        responseRequest.Tools = [.. responseRequest.Tools ?? [],
            .. responseRequest.Metadata.GetResponseToolDefinitions(modelProvider.GetIdentifier()) ?? []];

        responseRequest.Reasoning ??= responseRequest.Metadata
            .GetProviderOption<Responses.Reasoning>(modelProvider.GetIdentifier(), "reasoning");
        responseRequest.Include ??= responseRequest.Metadata
            .GetProviderOption<List<string>>(modelProvider.GetIdentifier(), "include");

        responseRequest.Metadata = null;
    }

    public static void SetDefaultResponseProperties(
       this IModelProvider modelProvider, MessagesRequest messagesRequest)
    {
        messagesRequest.Tools = [.. messagesRequest.Tools ?? [],
            .. messagesRequest.Metadata?.GetMessageToolDefinitions(modelProvider.GetIdentifier()) ?? []];

        messagesRequest.Thinking ??= messagesRequest.Metadata?
            .GetProviderOption<MessagesThinkingConfig>(modelProvider.GetIdentifier(), "thinking");

        messagesRequest.Metadata?.AdditionalProperties = null;
    }

}
