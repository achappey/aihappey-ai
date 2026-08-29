using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsDocumentIntelligenceModel(request.Model))
            return await ExecuteDocumentIntelligenceUnifiedAsync(request, cancellationToken);

        if (IsTranslationModel(request.Model))
            return await ExecuteTranslationUnifiedAsync(request, cancellationToken);

        if (IsSpeechToTextModel(request.Model))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedSpeechAsync(request, cancellationToken);

        throw new NotSupportedException($"Azure unified model '{request.Model}' is not supported.");
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IAsyncEnumerable<AIStreamEvent> stream;
        if (IsDocumentIntelligenceModel(request.Model))
            stream = StreamDocumentIntelligenceUnifiedAsync(request, cancellationToken);
        else if (IsTranslationModel(request.Model))
            stream = StreamTranslationUnifiedAsync(request, cancellationToken);
        else if (IsSpeechToTextModel(request.Model))
            stream = this.StreamUnifiedTranscriptionAsync(request, cancellationToken);
        else if (await this.IsSpeechModelAsync(request.Model, cancellationToken))
            stream = this.StreamUnifiedSpeechAsync(request, cancellationToken);
        else
            throw new NotSupportedException($"Azure unified model '{request.Model}' is not supported.");

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

    private static bool IsTranslationModel(string? model)
        => GetProviderModelName(model).StartsWith("translate-to-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSpeechToTextModel(string? model)
        => string.Equals(GetProviderModelName(model), "speech-to-text", StringComparison.OrdinalIgnoreCase);

    private static string GetProviderModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var separator = model.IndexOf('/');
        return (separator >= 0 ? model[(separator + 1)..] : model).Trim();
    }
}
