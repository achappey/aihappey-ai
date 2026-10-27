using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RampRouter;

public partial class RampRouterProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteDecisionAsync(request, null, null, cancellationToken);
        var warnings = DecisionWarnings(result.Body);
        if (warnings.Count > 0)
            (result.Response.AdditionalProperties ??= new())["warnings"] = JsonSerializer.SerializeToElement(warnings);
        return result.Response;
    }

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        // The shared mapper intentionally ignores canonical provider options. Reject
        // them here rather than letting an attempted override disappear silently.
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && options.ValueKind == JsonValueKind.Object
            && options.EnumerateObject().Any(property =>
                new[] { "model", "input", "questions" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("RampRouter decision options cannot override model, input, or questions.", nameof(request));

        var native = request.ToOpenAIDecisionRequest(GetIdentifier());
        var result = await ExecuteDecisionAsync(native,
            native.Input.Messages is not null ? request.State.Value : null,
            request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);
        var response = result.ToDecisionResponse(request, GetIdentifier());
        response.Warnings = DecisionWarnings(result.Body);
        return response;
    }

    private Task<OpenAICompatibleDecisionResult> ExecuteDecisionAsync(
        OpenAIDecisionRequest request, JsonElement? rawInput,
        Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        var prefix = GetIdentifier() + "/";
        var model = request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? request.Model[prefix.Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var modelParts = model.Split(':');
        if (modelParts.Length >= 3 && modelParts[^1] is not ("auto" or "default"))
            throw new NotSupportedException("RampRouter Decisions does not support pinned service tiers other than auto or default.");

        foreach (var (name, value) in request.AdditionalProperties ?? [])
        {
            switch (name)
            {
                case "metadata" when value.ValueKind == JsonValueKind.Object:
                case "stream" when value.ValueKind == JsonValueKind.False:
                case "service_tier" when value.ValueKind == JsonValueKind.String && value.GetString() is "auto" or "default":
                    break;
                default:
                    throw new NotSupportedException($"RampRouter Decisions does not support option '{name}' with this value. Fallback models, streaming, flex tiers, and routing strategies are not supported by this integration.");
            }
        }

        var isJev = modelParts.Any(part => part[(part.LastIndexOf('/') + 1)..].StartsWith("jev-", StringComparison.OrdinalIgnoreCase));
        if (isJev)
        {
            if (request.Questions.Any(question => string.IsNullOrWhiteSpace(question.Name))
                || request.Questions.Select(question => question.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
                throw new ArgumentException("RampRouter Jev decision questions require unique, nonempty names.", nameof(request));
            if (request.Input.Messages?.Any(message =>
                message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
                throw new NotSupportedException("RampRouter Jev Decisions does not support image inputs.");
        }

        var native = new OpenAIDecisionRequest
        {
            Model = model, Input = request.Input, Questions = request.Questions,
            SafetyIdentifier = request.SafetyIdentifier, AdditionalProperties = request.AdditionalProperties
        };
        ApplyAuthHeader();
        return ModelProviderDecisionExtensions.SendDecisionRequestAsync(
            _client, native, "v1/decisions", rawInput, headers, cancellationToken, NormalizeDecisionResponse);
    }

    private static JsonElement NormalizeDecisionResponse(JsonElement body)
    {
        if (JsonNode.Parse(body.GetRawText()) is not JsonObject normalized)
            throw new JsonException("RampRouter decision response must be an object.");
        if (normalized["usage"] is JsonObject usage)
        {
            if (usage["input_tokens"] is not JsonValue input || !input.TryGetValue<int>(out var inputTokens)
                || usage["output_tokens"] is not JsonValue output || !output.TryGetValue<int>(out var outputTokens)
                || inputTokens < 0 || outputTokens < 0 || (long)inputTokens + outputTokens > int.MaxValue)
                throw new JsonException("RampRouter decision usage requires nonnegative token counts.");
            if (!usage.ContainsKey("total_tokens"))
                usage["total_tokens"] = inputTokens + outputTokens;
        }
        if (normalized["answers"] is JsonArray answers)
            foreach (var answer in answers.OfType<JsonObject>())
                if (answer["type"]?.ToString() is "choice" or "score" && !answer.ContainsKey("confidence"))
                    answer["confidence"] = 0;
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static List<object> DecisionWarnings(JsonElement body)
    {
        List<object> warnings = [];
        if (body.GetProperty("answers").EnumerateArray().Any(answer =>
            answer.GetProperty("type").GetString() is "choice" or "score" && !answer.TryGetProperty("confidence", out _)))
            warnings.Add(new
            {
                type = "other",
                message = "RampRouter omitted confidence; the required OpenAI-compatible confidence field defaults to zero, not an inferred confidence."
            });
        return warnings;
    }
}
