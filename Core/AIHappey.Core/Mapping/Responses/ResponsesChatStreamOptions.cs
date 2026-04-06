using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using System.Text.Json;

namespace AIHappey.Core.AI;

public sealed class ResponsesChatStreamOptions
{
    public string Url { get; init; } = "v1/responses";

    public ResponsesRequestMappingOptions? RequestMappingOptions { get; init; }

    public ResponsesStreamMappingOptions? StreamMappingOptions { get; init; }

    public Func<ChatRequest, ResponseRequest>? RequestFactory { get; init; }

    public Action<ResponseRequest>? RequestMutator { get; init; }

    public Func<ChatRequest, JsonElement?>? ExtraRootPropertiesFactory { get; init; }

    public bool EmitErrorPartOnException { get; init; } = true;

    public Func<Exception, UIMessagePart>? ExceptionMapper { get; init; }

    public Func<UIMessagePart, CancellationToken, IAsyncEnumerable<UIMessagePart>>? PartPostProcessor { get; init; }
}
