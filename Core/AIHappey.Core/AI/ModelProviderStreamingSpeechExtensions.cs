using AIHappey.Core.Contracts;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderStreamingSpeechExtensions
{
    /// <summary>
    /// Placeholder for Vercel speech streaming until it becomes a provider capability.
    /// Deliberately does not fall back to non-streaming synthesis or merge text parts.
    /// </summary>
    public static IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        this IModelProvider modelProvider,
        StreamingSpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            $"Provider '{modelProvider.GetIdentifier()}' does not support Vercel speech streaming yet.");
    }
}
