using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Sference;

public sealed partial class SferenceProvider
{
    public async Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sdkState = request.StateFormat == "sdk";
        request = request.PrepareForProvider(GetIdentifier(), requiresInstructions: false);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw Unsupported("Decisions provider options; Decisions is realtime only");
        if (request.Questions.Count > 64)
            throw new ArgumentException("Sference Decisions accepts 1-64 questions.");
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            var native = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type
            };
            if (question.Instructions is not null)
                native["instructions"] = DecisionText(question.Instructions);
            switch (question)
            {
                case DecisionChoiceQuestion choice:
                    ValidateCriteriaCount(choice.Criteria.Count);
                    var criteria = new JsonObject();
                    foreach (var (label, description) in choice.Criteria)
                        criteria[label] = description is null ? label : DecisionText(description);
                    native["criteria"] = criteria;
                    break;
                case DecisionScoreQuestion score:
                    ValidateCriteriaCount(score.Criteria.Count);
                    native["criteria"] = new JsonArray(score.Criteria.Select((level, index) =>
                        (JsonNode?)JsonValue.Create(level is null ? Index(index) : DecisionText(level))).ToArray());
                    break;
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    native["criteria"] = JsonSerializer.SerializeToNode(new
                    {
                        @true = boolean.Criteria.True is null ? "true" : DecisionText(boolean.Criteria.True),
                        @false = boolean.Criteria.False is null ? "false" : DecisionText(boolean.Criteria.False)
                    });
                    break;
            }
            questions[id] = native;
        }

        var state = request.State.Value;
        if (sdkState)
            RejectDecisionImages(state);
        var body = new JsonObject
        {
            ["model"] = NativeModel(request.Model),
            ["state"] = JsonNode.Parse(state.GetRawText()),
            ["questions"] = questions
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        if (bytes.Length > 1024 * 1024)
            throw new ArgumentException("Sference Decisions request body exceeds 1 MiB.");

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new ByteArrayContent(bytes)
        };
        message.Content.Headers.ContentType = new("application/json");
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await ReadResponseAsync(response, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var nativeAnswers = root.GetProperty("answers");
            if (nativeAnswers.ValueKind != JsonValueKind.Object
                || nativeAnswers.EnumerateObject().Count() != request.Questions.Count)
                throw new JsonException("Sference Decisions must return one answer per question.");
            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (var (id, question) in request.Questions)
            {
                var answer = nativeAnswers.GetProperty(id);
                var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
                if (answer.GetProperty("type").GetString() != expectedType)
                    throw new JsonException("Sference decision answer type does not match the question.");
                answers[id] = question switch
                {
                    DecisionBooleanQuestion => new DecisionBooleanAnswer { Probability = Probability(answer.GetProperty("noul")) },
                    DecisionChoiceQuestion choice => MapChoice(answer, choice),
                    DecisionScoreQuestion score => MapScore(answer, score),
                    _ => throw new JsonException("Unsupported Sference decision answer.")
                };
            }
            var model = root.GetProperty("model").GetString();
            if (string.IsNullOrWhiteSpace(model))
                throw new JsonException("Sference Decisions response requires a model.");
            return new DecisionResponse
            {
                Answers = answers,
                Usage = new() { InputTokens = UsageCount(root, "input_tokens"), OutputTokens = UsageCount(root, "output_tokens") },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
                Response = new()
                {
                    ModelId = model.ToModelId(GetIdentifier()),
                    Timestamp = DateTimeOffset.UtcNow,
                    Headers = response.GetHeaders().ToDictionary(p => p.Key, p => (string?)p.Value),
                    Body = root.Clone()
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Sference returned an invalid Decisions response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties is { Count: > 0 })
            throw Unsupported("safety_identifier or additional Decisions fields");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw Unsupported("image input on Decisions");

        var questions = new Dictionary<string, DecisionQuestion>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var source = request.Questions[index];
            DecisionQuestion question = source switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion(),
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    // Synthetic labels keep typed values such as true and "true" distinct.
                    Criteria = choice.Choices.Select((item, i) => (item, i)).ToDictionary(
                        pair => Index(pair.i), pair => (DecisionInput?)new DecisionInput(
                            pair.item.Description is null ? pair.item.Value.Value.GetRawText()
                                : $"{pair.item.Value.Value.GetRawText()}: {pair.item.Description}"))
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        level.Description is null ? level.Label : $"{level.Label}: {level.Description}")).ToArray()
                },
                _ => throw Unsupported($"Decisions question '{source.Type}'")
            };
            question.Instructions = new DecisionInput(source.Instructions);
            questions[Index(index)] = question;
        }

        var nativeRequest = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(request.Input.Messages, Json)),
            Questions = questions
        };
        var result = await DecisionRequestAsync(nativeRequest, cancellationToken);
        var nativeBody = (JsonElement)result.Response!.Body!;
        var nativeAnswers = nativeBody.GetProperty("answers");
        var answers = request.Questions.Select((question, index) =>
        {
            var id = Index(index);
            var answer = result.Answers[id];
            var raw = nativeAnswers.GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, answer) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice, CultureInfo.InvariantCulture)].Value,
                    Confidence = Probability(raw.GetProperty("confidence")),
                    Probabilities = choice.Choices.Select((item, i) => new OpenAIDecisionChoiceProbability
                    {
                        Value = item.Value, Probability = selected.Probabilities![Index(i)]
                    }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer selected) => new OpenAIDecisionScoreAnswer
                {
                    Score = selected.Score,
                    Confidence = Probability(raw.GetProperty("confidence")),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    {
                        Label = level.Label, Value = i, Probability = selected.Probabilities![Index(i)]
                    }).ToArray()
                },
                _ => throw new InvalidOperationException("Sference decision answer type mismatch.")
            };
            mapped.Name = question.Name;
            return mapped;
        }).ToArray();
        return new OpenAIDecisionResponse
        {
            Model = nativeBody.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = result.Usage!.InputTokens!.Value,
                OutputTokens = result.Usage.OutputTokens!.Value,
                TotalTokens = checked(result.Usage.InputTokens.Value + result.Usage.OutputTokens.Value)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(result.ProviderMetadata, Json)
            }
        };
    }

    private static string DecisionText(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static void ValidateCriteriaCount(int count)
    {
        if (count is < 1 or > 256)
            throw new ArgumentException("Sference Decisions requires 1-256 criteria per choice or score question.");
    }

    private static void RejectDecisionImages(JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Array)
            foreach (var item in state.EnumerateArray()) RejectDecisionImages(item);
        else if (state.ValueKind == JsonValueKind.Object)
        {
            if (ReadString(state, "type") == "input_image")
                throw Unsupported("image input on Decisions");
            foreach (var property in state.EnumerateObject()) RejectDecisionImages(property.Value);
        }
    }

    private static DecisionChoiceAnswer MapChoice(JsonElement answer, DecisionChoiceQuestion question)
    {
        var probabilities = Distribution(answer.GetProperty("probabilities"), question.Criteria.Keys);
        var choice = answer.GetProperty("choice").GetString()!;
        if (!probabilities.ContainsKey(choice))
            throw new JsonException("Sference selected an unknown decision option.");
        _ = Probability(answer.GetProperty("confidence"));
        return new() { Choice = choice, Probabilities = probabilities };
    }

    private static DecisionScoreAnswer MapScore(JsonElement answer, DecisionScoreQuestion question)
    {
        var probabilities = Distribution(answer.GetProperty("probabilities"), Enumerable.Range(0, question.Criteria.Count).Select(Index));
        var score = answer.GetProperty("score").GetDouble();
        if (!double.IsFinite(score) || score < 0 || score > question.Criteria.Count - 1)
            throw new JsonException("Sference decision score is outside the submitted levels.");
        _ = Probability(answer.GetProperty("confidence"));
        return new() { Score = score, Probabilities = probabilities };
    }

    private static Dictionary<string, double> Distribution(JsonElement element, IEnumerable<string> labels)
    {
        var expected = labels.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, Probability(item.Value)))
                throw new JsonException("Sference returned unknown or duplicate decision options.");
        // The documented API examples round probabilities to four decimal places.
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > expected.Count * 0.00005 + 0.000001)
            throw new JsonException("Sference decision probabilities must cover all criteria and sum to one.");
        return result;
    }

    private static double Probability(JsonElement element)
    {
        var probability = element.GetDouble();
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new JsonException("Sference decision probabilities must be in [0, 1].");
        return probability;
    }

    private static int UsageCount(JsonElement root, string name)
    {
        var count = root.GetProperty("usage").GetProperty(name).GetInt32();
        if (count < 0) throw new JsonException("Sference decision token counts must be nonnegative.");
        return count;
    }
}
