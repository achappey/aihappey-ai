using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Core.Contracts;
using AIHappey.Responses;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

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

}
