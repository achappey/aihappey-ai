using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    private const string StraicoRagModelPrefix = "rag/";

    private static bool IsStraicoRagModel(string? model)
        => TryResolveStraicoRagModel(model, out _, out _);

    private static bool TryResolveStraicoRagModel(string? model, out string ragId, out string baseModel)
    {
        ragId = string.Empty;
        baseModel = string.Empty;
        var local = NormalizeStraicoShortcutModel(model);

        if (!local.StartsWith(StraicoRagModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var ragAndModel = local[StraicoRagModelPrefix.Length..];
        var separator = ragAndModel.IndexOf('/');
        if (separator <= 0 || separator >= ragAndModel.Length - 1)
            return false;

        ragId = ragAndModel[..separator].Trim('/');
        baseModel = ragAndModel[(separator + 1)..].Trim('/');
        return !string.IsNullOrWhiteSpace(ragId) && !string.IsNullOrWhiteSpace(baseModel);
    }

    private async Task<AIResponse> ExecuteStraicoRagUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveStraicoRagModel(request.Model, out var ragId, out var model))
            throw new InvalidOperationException($"Straico RAG model '{request.Model}' must use 'straico/rag/{{ragId}}/{{baseModelId}}'.");

        ApplyAuthHeader();

        var prompt = ExtractStraicoLastUserTextPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Straico RAG requests require text parts in the last user message.", nameof(request));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v0/rag/{Uri.EscapeDataString(ragId)}/prompt")
        {
            Content = BuildStraicoPromptFormContent(prompt, model, request)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"Straico RAG request failed ({(int)response.StatusCode})."
                : $"Straico RAG request failed ({(int)response.StatusCode}): {body}");

        using var document = ParseStraicoPromptResponse(body);
        return CreateStraicoPromptUnifiedResponse(request, document.RootElement.Clone(), "rag", ragId, model, prompt);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamStraicoRagUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteStraicoRagUnifiedAsync(request, cancellationToken);

        foreach (var streamEvent in CreateStraicoSyntheticTextStream(request, response, "straico.rag.raw"))
            yield return streamEvent;
    }
}
