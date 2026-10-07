using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>First-round Decisions surface only; providers do not execute decisions yet.</summary>
public static class ModelProviderDecisionExtensions
{
    public static Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        this IModelProvider modelProvider,
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OpenAI-compatible Decisions are not supported yet.");

    public static Task<DecisionResponse> DecisionRequestAsync(
        this IModelProvider modelProvider,
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Vercel-compatible Decisions are not supported yet.");
}
