using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

/// <summary>Reusable transport and mapping for OpenAI-compatible Decisions endpoints.</summary>
public static partial class ModelProviderDecisionExtensions
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Returns the native contract, without qualifying or rewriting the upstream model.</summary>
    public static async Task<OpenAIDecisionResponse> OpenAICompatibleDecisionRequestAsync(
        this HttpClient httpClient,
        OpenAIDecisionRequest options,
        string? endpoint = "v1/decisions",
        CancellationToken cancellationToken = default)
        => (await httpClient.OpenAICompatibleDecisionResultAsync(options, endpoint, cancellationToken)).Response;

    /// <summary>Native response together with the unmodified JSON and upstream headers.</summary>
    public static Task<OpenAICompatibleDecisionResult> OpenAICompatibleDecisionResultAsync(
        this HttpClient httpClient,
        OpenAIDecisionRequest options,
        string? endpoint = "v1/decisions",
        CancellationToken cancellationToken = default)
        => SendDecisionRequestAsync(httpClient, options, endpoint, null, null, cancellationToken);

    public static async Task<DecisionResponse> OpenAICompatibleVercelDecisionRequestAsync(
        this HttpClient httpClient,
        DecisionRequest request,
        string providerIdentifier,
        string? endpoint = "v1/decisions",
        CancellationToken cancellationToken = default)
    {
        var native = request.ToOpenAIDecisionRequest(providerIdentifier);
        // Recognition and validation already occurred during mapping. Use the original
        // JSON for message arrays so even explicit null detail fields remain unchanged.
        JsonElement? rawInput = native.Input.Messages is not null ? request.State.Value : null;
        var headers = request.Headers?.GetProviderPassthroughHeaders(providerIdentifier);
        var result = await SendDecisionRequestAsync(
            httpClient, native, endpoint, rawInput, headers, cancellationToken);
        return result.ToDecisionResponse(request, providerIdentifier);
    }

    private static async Task<OpenAICompatibleDecisionResult> SendDecisionRequestAsync(
        HttpClient httpClient,
        OpenAIDecisionRequest options,
        string? endpoint,
        JsonElement? rawInput,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateOpenAIDecisionRequest(options);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = JsonSerializer.SerializeToNode(options, DecisionJsonOptions)!.AsObject();
        if (rawInput.HasValue)
            payload["input"] = JsonNode.Parse(rawInput.Value.GetRawText());

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var header in headers ?? [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Decision request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            ValidateDecisionResponseShape(root, options);
            var result = root.Deserialize<OpenAIDecisionResponse>(DecisionJsonOptions)
                ?? throw new JsonException("The response body was null.");
            return new(result, response.GetHeaders(), root.Clone());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Decision request returned an invalid OpenAI-compatible response.", exception);
        }
    }

    private static void ValidateDecisionResponseShape(JsonElement root, OpenAIDecisionRequest request)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Array
            || answers.GetArrayLength() != request.Questions.Count
            || !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("A decision response requires model, one answer per question, and usage.");

        RequireInteger(usage, "input_tokens");
        RequireInteger(usage, "output_tokens");
        RequireInteger(usage, "total_tokens");
        if (!usage.TryGetProperty("input_tokens_details", out var inputDetails) || inputDetails.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("output_tokens_details", out var outputDetails) || outputDetails.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision usage requires input/output token details.");
        RequireInteger(inputDetails, "cached_tokens");
        RequireInteger(inputDetails, "cache_write_tokens");
        RequireInteger(outputDetails, "reasoning_tokens");

        var index = 0;
        foreach (var answer in answers.EnumerateArray())
        {
            var question = request.Questions[index++];
            if (answer.ValueKind != JsonValueKind.Object
                || !answer.TryGetProperty("name", out var name) || name.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
                || name.GetString() != question.Name
                || !answer.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || (type.GetString() != "refusal" && type.GetString() != question.Type))
                throw new JsonException("Decision answer names and types must match questions in order.");

            switch (type.GetString())
            {
                case "predicate": RequireProbability(answer, "probability"); break;
                case "choice":
                    RequireProbability(answer, "confidence");
                    if (!answer.TryGetProperty("choice", out var choice) || !IsChoiceValue(choice))
                        throw new JsonException("A decision choice must be a string or boolean.");
                    ValidateRawProbabilities(answer, "value", true);
                    break;
                case "score":
                    RequireNumber(answer, "score");
                    RequireProbability(answer, "confidence");
                    ValidateRawProbabilities(answer, "label", false);
                    break;
            }
        }
    }

    private static void ValidateRawProbabilities(JsonElement answer, string key, bool choice)
    {
        if (!answer.TryGetProperty("probabilities", out var probabilities) || probabilities.ValueKind != JsonValueKind.Array)
            throw new JsonException("Decision choice and score answers require probabilities.");
        foreach (var item in probabilities.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value)
                || (choice ? !IsChoiceValue(value) : value.ValueKind != JsonValueKind.String))
                throw new JsonException("Invalid decision probability key.");
            RequireProbability(item, "probability");
            if (!choice)
                RequireNumber(item, "value");
        }
    }

    private static void RequireInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out _))
            throw new JsonException($"Decision response requires integer '{name}'.");
    }

    private static double RequireNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException($"Decision response requires finite number '{name}'.");
        return number;
    }

    private static void RequireProbability(JsonElement root, string name)
    {
        if (RequireNumber(root, name) is < 0 or > 1)
            throw new JsonException($"Decision probability '{name}' must be in [0, 1].");
    }
}

public sealed record OpenAICompatibleDecisionResult(
    OpenAIDecisionResponse Response,
    IDictionary<string, string> Headers,
    JsonElement Body);
