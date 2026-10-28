using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MachineFi;

public sealed partial class MachineFiProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ValidateModel(request.Model);

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && (providerOptions.ValueKind != JsonValueKind.Object || providerOptions.EnumerateObject().Any()))
            throw new NotSupportedException("MachineFi Decisions does not support provider options.");
        if (request.Questions.Count != 1
            || request.Questions.Single().Value is not DecisionChoiceQuestion { Criteria.Count: >= 2 and <= 8 } question)
            throw new NotSupportedException("Trio-Spark Decisions requires one choice question with 2 to 8 choices.");

        var choices = question.Criteria.Select(choice => new NativeChoice(
            choice.Key,
            choice.Value is null ? null : RenderDecisionText(choice.Value.Value))).ToArray();
        var task = RenderDecisionText(question.Instructions!.Value);
        var result = await SendDecisionAsync(
            task,
            request.State.Value,
            choices,
            request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()),
            cancellationToken);

        return new DecisionResponse
        {
            Answers = new()
            {
                [request.Questions.Single().Key] = new DecisionChoiceAnswer
                {
                    Choice = result.ChoiceId,
                    Probabilities = result.Probabilities
                }
            },
            Usage = result.Usage,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = result.Body
            },
            Response = new()
            {
                ModelId = result.Model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = result.RequestId,
                Headers = result.Headers,
                Body = result.Body
            }
        };
    }

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateModel(request.Model);

        if (request.Questions.Count != 1
            || request.Questions[0] is not OpenAIDecisionChoiceQuestion { Choices.Count: >= 2 and <= 8 } question
            || question.Choices.Any(choice => choice.Value.Value.ValueKind != JsonValueKind.String))
            throw new NotSupportedException("Trio-Spark Decisions requires one choice question with 2 to 8 string choices.");
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("MachineFi Decisions does not support safety_identifier or additional request fields.");

        var state = JsonSerializer.SerializeToElement(request.Input, JsonOptions);
        var choices = question.Choices.Select(choice => new NativeChoice(
            choice.Value.Value.GetString()!,
            choice.Description)).ToArray();
        var result = await SendDecisionAsync(
            question.Instructions,
            state,
            choices,
            null,
            cancellationToken);

        return new OpenAIDecisionResponse
        {
            Model = result.Model,
            Answers =
            [
                new OpenAIDecisionChoiceAnswer
                {
                    Name = question.Name,
                    Choice = new OpenAIDecisionChoiceValue(result.ChoiceId),
                    Confidence = result.Probabilities.GetValueOrDefault(result.ChoiceId),
                    Probabilities = result.Probabilities.Select(item => new OpenAIDecisionChoiceProbability
                    {
                        Value = new OpenAIDecisionChoiceValue(item.Key),
                        Probability = item.Value
                    }).ToArray()
                }
            ],
            Usage = new OpenAIDecisionUsage
            {
                InputTokens = result.Usage.InputTokens ?? 0,
                OutputTokens = result.Usage.OutputTokens ?? 0,
                TotalTokens = (result.Usage.InputTokens ?? 0) + (result.Usage.OutputTokens ?? 0)
            }
        };
    }

    private async Task<NativeDecisionResult> SendDecisionAsync(
        string task,
        JsonElement state,
        IReadOnlyList<NativeChoice> choices,
        Dictionary<string, string>? passthroughHeaders,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var payload = new JsonObject
        {
            ["model"] = NativeModel,
            ["task"] = task,
            ["state"] = JsonNode.Parse(state.GetRawText()),
            ["choices"] = JsonSerializer.SerializeToNode(choices, JsonOptions)
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/spark/v1/decisions")
        {
            Content = new StringContent(payload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
        };
        foreach (var (name, value) in passthroughHeaders ?? [])
        {
            if (!name.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(name, value);
        }

        var idempotencyKey = passthroughHeaders?.FirstOrDefault(
            header => header.Key.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase)).Value;
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString() : idempotencyKey);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders().ToDictionary(pair => pair.Key, pair => pair.Value);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MachineFi Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            return ParseDecisionResponse(document.RootElement, choices, headers);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("MachineFi returned an invalid Trio-Spark Decisions response.", exception);
        }
    }

    private static NativeDecisionResult ParseDecisionResponse(
        JsonElement body,
        IReadOnlyList<NativeChoice> choices,
        IReadOnlyDictionary<string, string> headers)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision response must be an object.");

        var choiceId = ReadChoiceId(body);
        if (!choices.Any(choice => choice.Id == choiceId))
            throw new JsonException("Decision response selected a choice that was not submitted.");

        var probabilities = ReadProbabilities(body, choices);
        if (!string.IsNullOrWhiteSpace(choiceId) && probabilities.Count != choices.Count)
            throw new JsonException("Decision response must include probabilities for every submitted choice.");
        if (Math.Abs(probabilities.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Decision probabilities must sum to one.");

        var model = ReadString(body, "model") ?? NativeModel;
        var usage = ReadUsage(body);
        var clonedBody = body.Clone();
        return new NativeDecisionResult(
            model,
            choiceId,
            probabilities,
            usage,
            headers.GetValueOrDefault("x-request-id"),
            headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            clonedBody);
    }

    private static string ReadChoiceId(JsonElement body)
    {
        if (body.TryGetProperty("choice_id", out var choiceId)
            && choiceId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(choiceId.GetString()))
            return choiceId.GetString()!;
        if (body.TryGetProperty("choice", out var choice))
        {
            if (choice.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(choice.GetString()))
                return choice.GetString()!;
            if (choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString()!;
        }

        throw new JsonException("Decision response must include a selected choice ID.");
    }

    private static Dictionary<string, double> ReadProbabilities(
        JsonElement body,
        IReadOnlyList<NativeChoice> choices)
    {
        if (!body.TryGetProperty("probabilities", out var probabilities))
            throw new JsonException("Decision response must include a probability distribution.");

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (probabilities.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in probabilities.EnumerateObject())
                AddProbability(result, item.Name, item.Value);
        }
        else if (probabilities.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in probabilities.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !(item.TryGetProperty("id", out var id)
                         || item.TryGetProperty("choice_id", out id))
                    || id.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("probability", out var probability))
                    throw new JsonException("Invalid decision probability entry.");
                AddProbability(result, id.GetString()!, probability);
            }
        }
        else
        {
            throw new JsonException("Decision probabilities must be an object or array.");
        }

        if (result.Keys.Any(key => choices.All(choice => choice.Id != key)))
            throw new JsonException("Decision probabilities contain an unknown choice ID.");
        return result;
    }

    private static void AddProbability(Dictionary<string, double> result, string id, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(id)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var probability)
            || !double.IsFinite(probability)
            || probability is < 0 or > 1
            || !result.TryAdd(id, probability))
            throw new JsonException("Decision probabilities must contain unique choice IDs and values in [0, 1].");
    }

    private static DecisionUsage ReadUsage(JsonElement body)
    {
        if (!body.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision response must include usage.");
        return new()
        {
            InputTokens = ReadCount(usage, "input_tokens", "inputTokens"),
            OutputTokens = ReadCount(usage, "output_tokens", "outputTokens")
        };
    }

    private static int? ReadCount(JsonElement element, string snakeCase, string camelCase)
    {
        if (element.TryGetProperty(snakeCase, out var value)
            || element.TryGetProperty(camelCase, out value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
                throw new JsonException($"Usage '{snakeCase}' must be a nonnegative integer.");
            return count;
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string RenderDecisionText(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static void ValidateModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));
        var nativeModel = model.StartsWith("machinefi/", StringComparison.OrdinalIgnoreCase)
            ? model["machinefi/".Length..]
            : model;
        if (!string.Equals(nativeModel, NativeModel, StringComparison.Ordinal))
            throw new NotSupportedException($"MachineFi Decisions does not support model '{model}'.");
    }

    private sealed record NativeChoice(string Id, string? Description);

    private sealed record NativeDecisionResult(
        string Model,
        string ChoiceId,
        Dictionary<string, double> Probabilities,
        DecisionUsage Usage,
        string? RequestId,
        IDictionary<string, string?> Headers,
        JsonElement Body);
}
