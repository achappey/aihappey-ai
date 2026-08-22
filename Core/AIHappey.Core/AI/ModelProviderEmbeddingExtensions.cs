using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>
/// Forward-compatible embedding and live-transcription seams. Providers can
/// replace these stubs with concrete extension overloads without expanding
/// IModelProvider yet.
/// </summary>
public static class ModelProviderEmbeddingExtensions
{
    public static Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        this IModelProvider modelProvider,
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Provider '{modelProvider.GetIdentifier()}' does not implement OpenAI-compatible embeddings yet.");

    public static Task<EmbeddingResponse> EmbeddingRequestAsync(
        this IModelProvider modelProvider,
        EmbeddingRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"Provider '{modelProvider.GetIdentifier()}' does not implement Vercel AI SDK embeddings yet.");

    public static async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        this IModelProvider modelProvider,
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"Provider '{modelProvider.GetIdentifier()}' does not implement live transcription streaming yet.");
    }
}
