using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public sealed class ResponsesStreamMappingOptions
{
    public object? StructuredOutputs { get; init; }

    public bool EmitStartStepOnCreated { get; init; } = true;

    public bool EmitToolApprovalRequests { get; init; } = true;

    public bool StartTextOnDeltaIfMissing { get; init; } = true;

    public IEnumerable<string>? ProviderExecutedTools { get; init; }

    public Func<string, string?>? ResolveToolTitle { get; init; }

    public Func<ResponseStreamAnnotation, CancellationToken, IAsyncEnumerable<UIMessagePart>>? AnnotationMapper { get; init; }

    public Func<ResponseStreamItem, CancellationToken, IAsyncEnumerable<UIMessagePart>>? OutputItemMapper { get; init; }

    public Func<ResponseOutputItemDone, ResponsesStreamMappingContext, CancellationToken, IAsyncEnumerable<UIMessagePart>>? OutputItemDoneMapper { get; init; }

    public Func<ResponseUnknownEvent, ResponsesStreamMappingContext, CancellationToken, IAsyncEnumerable<UIMessagePart>>? UnknownEventMapper { get; init; }

    public Func<ResponseResult, CancellationToken, IAsyncEnumerable<UIMessagePart>>? BeforeFinishMapper { get; init; }

    public Func<ResponseResult, FinishUIPart>? FinishFactory { get; init; }

    public Func<ResponseResult, IEnumerable<UIMessagePart>>? FailedResponseFactory { get; init; }

    public Func<string, ResponseStreamItem?, bool?>? ProviderExecutedResolver { get; init; }

    public Func<string, ResponseStreamItem?, bool?>? ToolApprovalResolver { get; init; }
}
