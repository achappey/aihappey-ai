using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AbacusAI;

public sealed partial class AbacusAIProvider
{
    private static readonly JsonSerializerOptions AbacusDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateDecisionRequest(request);

        ApplyAuthHeader();
        var result = await ModelProviderDecisionExtensions.SendDecisionRequestAsync(
            _client,
            request,
            "v1/decisions",
            null,
            null,
            cancellationToken,
            NormalizeDecisionUsage);
        return result.Response;
    }

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && (providerOptions.ValueKind != JsonValueKind.Object || providerOptions.EnumerateObject().Any()))
            throw new NotSupportedException("AbacusAI Decisions does not support provider options.");

        var native = request.ToOpenAIDecisionRequest(GetIdentifier());
        ValidateDecisionRequest(native);
        var rawInput = native.Input.Messages is not null ? request.State.Value : (JsonElement?)null;

        ApplyAuthHeader();
        var result = await ModelProviderDecisionExtensions.SendDecisionRequestAsync(
            _client,
            native,
            "v1/decisions",
            rawInput,
            request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()),
            cancellationToken,
            NormalizeDecisionUsage);
        return result.ToDecisionResponse(request, GetIdentifier());
    }

    private static void ValidateDecisionRequest(OpenAIDecisionRequest request)
    {
        request.Model = GetNativeDecisionModel(request.Model);
        if (request.Model is not ("gpt-6-luna" or "jev-latest"))
            throw new NotSupportedException($"AbacusAI Decisions does not support model '{request.Model}'.");

        if (request.Questions.Count is < 1 or > 10)
            throw new ArgumentException("AbacusAI Decisions requires between 1 and 10 questions.", nameof(request));

        if (request.Questions.Any(question => string.IsNullOrWhiteSpace(question.Name))
            || request.Questions.Select(question => question.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new ArgumentException("AbacusAI Decisions requires unique, non-empty question names.", nameof(request));

        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("AbacusAI Decisions does not support safety_identifier or additional request fields.");

        if (SerializeLength(request.Input) > 512_000)
            throw new ArgumentException("AbacusAI Decisions input exceeds 512,000 serialized characters.", nameof(request));
        if (SerializeLength(request.Questions) > 512_000)
            throw new ArgumentException("AbacusAI Decisions questions exceed 512,000 serialized characters.", nameof(request));

        if (request.Input.Text is { Length: 0 })
            throw new ArgumentException("AbacusAI Decisions input cannot be empty.", nameof(request));

        if (request.Model == "jev-latest"
            && (request.Input.Messages?.Any(message =>
                message.Content.Text is null
                && message.Content.Parts?.Any(part => part is not OpenAIDecisionInputText) != false) == true))
            throw new NotSupportedException("AbacusAI jev-latest Decisions supports text input only.");
    }

    private static int SerializeLength<T>(T value)
        => JsonSerializer.SerializeToElement(value, AbacusDecisionJsonOptions).GetRawText().Length;

    private static string GetNativeDecisionModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        const string prefix = "abacusai/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private static JsonElement NormalizeDecisionUsage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("usage", out var nativeUsage)
            || nativeUsage.ValueKind != JsonValueKind.Object)
            return response.Clone();

        var usage = JsonNode.Parse(nativeUsage.GetRawText())!.AsObject();
        if (!usage.ContainsKey("output_tokens"))
            usage["output_tokens"] = 0;

        if (!usage.ContainsKey("total_tokens")
            && nativeUsage.TryGetProperty("input_tokens", out var input)
            && input.TryGetInt32(out var inputTokens))
        {
            var outputTokens = nativeUsage.TryGetProperty("output_tokens", out var output)
                && output.TryGetInt32(out var reportedOutputTokens)
                    ? reportedOutputTokens
                    : 0;
            try
            {
                usage["total_tokens"] = checked(inputTokens + outputTokens);
            }
            catch (OverflowException exception)
            {
                throw new JsonException("Decision usage token counts exceed the supported range.", exception);
            }
        }

        var normalized = JsonNode.Parse(response.GetRawText())!.AsObject();
        normalized["usage"] = usage;
        using var document = JsonDocument.Parse(normalized.ToJsonString());
        return document.RootElement.Clone();
    }
}
