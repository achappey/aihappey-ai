using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Messages.Mapping;
using AIHappey.ChatCompletions.Models;
using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.ChatCompletions.Mapping;

namespace AIHappey.Core.AI;

public static class ModelProviderResponsesChatExtensions
{

    public static void ApplyProviderOptions(
    this IModelProvider provider,
    Dictionary<string, object?>? metadata,
    IDictionary<string, JsonElement>? additional, HashSet<string>? exclude = null)
    {
        if (metadata is null || additional is null)
            return;

        if (!metadata.TryGetValue(provider.GetIdentifier(), out var obj))
            return;

        if (obj is not JsonElement json)
            return;

        foreach (var prop in json.EnumerateObject())
        {
            if (exclude?.Contains(prop.Name) == true)
                continue;

            additional[prop.Name] = prop.Value;
        }
    }

    public static void SetDefaultResponseProperties(
        this IModelProvider modelProvider, ResponseRequest responseRequest, HashSet<string>? exclude = null)
    {
        responseRequest.Tools = [.. responseRequest.Tools ?? [],
            .. responseRequest.Metadata.GetResponseToolDefinitions(modelProvider.GetIdentifier()) ?? []];

        /*       responseRequest.Reasoning ??= responseRequest.Metadata
                   .GetProviderOption<Responses.Reasoning>(modelProvider.GetIdentifier(), "reasoning");
               responseRequest.Include ??= responseRequest.Metadata
                   .GetProviderOption<List<string>>(modelProvider.GetIdentifier(), "include");*/

        modelProvider.ApplyProviderOptions(responseRequest.Metadata, responseRequest.AdditionalProperties ??=
                [], [.. exclude ?? [], "tools"]);


        responseRequest.Metadata = null;
    }

    public static void SetDefaultInteractionProperties(
       this IModelProvider modelProvider, InteractionRequest responseRequest, HashSet<string>? exclude = null)
    {
        responseRequest.Tools = [.. responseRequest.Tools ?? [],
            .. responseRequest.Metadata.GetInteractionToolDefinitions(modelProvider.GetIdentifier())?
            .Where(p => responseRequest.Tools?.Any(e => e.Type == p.Type) != true) ?? []];

        modelProvider.ApplyProviderOptions(responseRequest.Metadata, responseRequest.AdditionalProperties ??=
                [], [.. exclude ?? [], "tools"]);

        responseRequest.Metadata = null;
    }

    public static void SetDefaultChatCompletionProperties(
        this IModelProvider modelProvider, ChatCompletionOptions chatCompletionOptions, HashSet<string>? exclude = null)
    {
        chatCompletionOptions.Tools = [.. chatCompletionOptions.Tools ?? [],
            .. chatCompletionOptions.Metadata.GetChatCompletionToolDefinitions(modelProvider.GetIdentifier()) ?? []];

        modelProvider.ApplyProviderOptions(chatCompletionOptions.Metadata, chatCompletionOptions.AdditionalProperties ??=
                [], [.. exclude ?? [], "tools"]);

        chatCompletionOptions.Metadata = null;
    }

    public static void SetDefaultMessagesProperties(
       this IModelProvider modelProvider, MessagesRequest messagesRequest)
    {
        messagesRequest.Tools = [.. messagesRequest.Tools ?? [],
            .. messagesRequest.Metadata?.GetMessageToolDefinitions(modelProvider.GetIdentifier())?
            .Where(p => messagesRequest.Tools?.Any(e => e.Type == p.Type) != true) ?? []];

        messagesRequest.Thinking ??= messagesRequest.Metadata?
            .GetProviderOption<MessagesThinkingConfig>(modelProvider.GetIdentifier(), "thinking");

        messagesRequest.ContextManagement ??= messagesRequest.Metadata?
            .GetProviderOption<object>(modelProvider.GetIdentifier(), "context_management");

        //  modelProvider.ApplyProviderOptions(messagesRequest.Metadata, messagesRequest.AdditionalProperties ??=
        //              [], ["tools", "anthorpic-beta"]);

        messagesRequest.MaxTokens ??= messagesRequest.Metadata?
           .GetProviderOption<int>(modelProvider.GetIdentifier(), "max_tokens");

        messagesRequest.Metadata?.AdditionalProperties = null;
    }

}
