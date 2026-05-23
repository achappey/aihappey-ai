using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Straico;

public partial class StraicoProvider
{
    private const string StraicoAgentModelPrefix = "agent/";

    private static bool IsStraicoAgentModel(string? model)
        => TryResolveStraicoAgentId(model, out _);

    private static bool TryResolveStraicoAgentId(string? model, out string agentId)
    {
        agentId = string.Empty;
        var local = NormalizeStraicoShortcutModel(model);

        if (!local.StartsWith(StraicoAgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        agentId = local[StraicoAgentModelPrefix.Length..].Trim('/');
        return !string.IsNullOrWhiteSpace(agentId);
    }

    private async Task<AIResponse> ExecuteStraicoAgentUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveStraicoAgentId(request.Model, out var agentId))
            throw new InvalidOperationException($"Straico agent model '{request.Model}' must use 'straico/agent/{{agentId}}'.");

        ApplyAuthHeader();

        var prompt = ExtractStraicoLastUserTextPrompt(request);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Straico agent requests require text parts in the last user message.", nameof(request));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v0/agent/{Uri.EscapeDataString(agentId)}/prompt")
        {
            Content = BuildStraicoPromptFormContent(prompt, model: null, request)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"Straico agent request failed ({(int)response.StatusCode})."
                : $"Straico agent request failed ({(int)response.StatusCode}): {body}");

        using var document = ParseStraicoPromptResponse(body);
        return CreateStraicoPromptUnifiedResponse(request, document.RootElement.Clone(), "agent", agentId, null, prompt);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamStraicoAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteStraicoAgentUnifiedAsync(request, cancellationToken);

        foreach (var streamEvent in CreateStraicoSyntheticTextStream(request, response, "straico.agent.raw"))
            yield return streamEvent;
    }
}
