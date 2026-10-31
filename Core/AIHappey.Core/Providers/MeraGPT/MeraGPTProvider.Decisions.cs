using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MeraGPT;

public sealed partial class MeraGPTProvider
{
    private const string DecisionsEndpoint = "v1/systemone";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record DecisionExecutionResult(
        OpenAIDecisionResponse Response,
        IReadOnlyDictionary<string, string> Headers,
        JsonElement Body);

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = DecisionRequestCompatibility.PrepareForProvider(request, GetIdentifier());
        if (request.ProviderOptions is { Count: > 0 })
            throw new NotSupportedException("MeraGPT Decisions does not support provider options.");
        if (request.Headers is { Count: > 0 })
            throw new NotSupportedException("MeraGPT Decisions does not document request header overrides.");

        var openAIRequest = request.ToOpenAIDecisionRequest(GetIdentifier());
        var nativeBooleanCriteria = new Dictionary<string, (string True, string False)>(StringComparer.Ordinal);
        var questions = openAIRequest.Questions.ToArray();
        var index = 0;
        foreach (var (name, question) in request.Questions)
        {
            if (question is DecisionBooleanQuestion { Criteria: not null } boolean)
            {
                var trueInput = boolean.Criteria.True
                    ?? throw new ArgumentException($"Boolean question '{name}' is missing its true criterion.", nameof(request));
                var falseInput = boolean.Criteria.False
                    ?? throw new ArgumentException($"Boolean question '{name}' is missing its false criterion.", nameof(request));
                nativeBooleanCriteria[name] = (RenderInput(trueInput), RenderInput(falseInput));
                questions[index] = new OpenAIDecisionPredicateQuestion
                {
                    Name = name,
                    Instructions = questions[index].Instructions
                };
            }
            index++;
        }
        openAIRequest.Questions = questions;

        var result = await ExecuteOpenAIDecisionResultAsync(
            openAIRequest,
            nativeBooleanCriteria,
            request.State.Value,
            cancellationToken);

        var answers = result.Response.Answers.ToArray();
        index = 0;
        foreach (var (name, question) in request.Questions)
        {
            if (question is DecisionBooleanQuestion { Criteria: not null })
            {
                var predicate = (OpenAIDecisionPredicateAnswer)answers[index];
                answers[index] = new OpenAIDecisionChoiceAnswer
                {
                    Name = name,
                    Choice = true,
                    Confidence = Math.Max(predicate.Probability, 1 - predicate.Probability),
                    Probabilities =
                    [
                        new() { Value = true, Probability = predicate.Probability },
                        new() { Value = false, Probability = 1 - predicate.Probability }
                    ]
                };
            }
            index++;
        }
        result.Response.Answers = answers;

        return new OpenAICompatibleDecisionResult(
            result.Response,
            result.Headers.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase),
            result.Body)
            .ToDecisionResponse(
                request,
                GetIdentifier());
    }

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
        => (await ExecuteOpenAIDecisionResultAsync(request, null, null, cancellationToken)).Response;

    private async Task<DecisionExecutionResult> ExecuteOpenAIDecisionResultAsync(
        OpenAIDecisionRequest request,
        IReadOnlyDictionary<string, (string True, string False)>? booleanCriteria,
        JsonElement? stateOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);

        var model = NormalizeModel(request.Model);
        if (model != "decider-1")
            throw new NotSupportedException($"MeraGPT Decisions does not support model '{request.Model}'.");
        if (request.SafetyIdentifier is not null || request.AdditionalProperties is { Count: > 0 })
            throw new NotSupportedException("MeraGPT Decisions does not support safety_identifier or additional request fields.");
        ValidateDecisionInput(request.Input);
        ValidateDecisionQuestionNames(request.Questions);
        if (request.Questions.Count > 64)
            throw new ArgumentException("MeraGPT Decisions supports at most 64 questions.", nameof(request));

        var nativeRequest = new JsonObject
        {
            ["model"] = model,
            ["state"] = MapDecisionState(request.Input, stateOverride),
            ["questions"] = MapNativeQuestions(request.Questions, booleanCriteria)
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, DecisionsEndpoint)
        {
            Content = new StringContent(nativeRequest.ToJsonString(DecisionJsonOptions), Encoding.UTF8, "application/json")
        };

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"MeraGPT Decisions failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var mappedResponse = MapDecisionResponse(body, request);
            return new DecisionExecutionResult(mappedResponse, response.GetHeaders(), body.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("MeraGPT returned an invalid Decisions response.", exception);
        }
    }

    private static void ValidateDecisionInput(OpenAIDecisionInput input)
    {
        if (input.Text is not null)
            return;

        foreach (var message in input.Messages ?? [])
        {
            foreach (var part in message.Content.Parts ?? [])
                if (part is not OpenAIDecisionInputText)
                    throw new NotSupportedException("MeraGPT Decisions accepts text-only state; image input is unsupported.");
        }
    }

    private static JsonNode MapDecisionState(OpenAIDecisionInput input, JsonElement? stateOverride)
    {
        if (stateOverride.HasValue)
            return JsonNode.Parse(stateOverride.Value.GetRawText())!;
        if (input.Text is not null)
            return JsonValue.Create(input.Text)!;

        return new JsonObject
        {
            ["messages"] = JsonNode.Parse(JsonSerializer.Serialize(input.Messages, DecisionJsonOptions))
        };
    }

    private static JsonObject MapNativeQuestions(
        IReadOnlyList<OpenAIDecisionQuestion> questions,
        IReadOnlyDictionary<string, (string True, string False)>? booleanCriteria)
    {
        var result = new JsonObject();
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            var name = question.Name!;
            var native = new JsonObject
            {
                ["type"] = question switch
                {
                    OpenAIDecisionPredicateQuestion => "noul",
                    OpenAIDecisionChoiceQuestion => "choice",
                    OpenAIDecisionScoreQuestion => "score",
                    _ => throw new NotSupportedException($"Unsupported MeraGPT Decisions question type '{question.Type}'.")
                },
                ["instructions"] = question.Instructions
            };

            if (question is OpenAIDecisionPredicateQuestion
                && booleanCriteria?.TryGetValue(name, out var criteria) == true)
            {
                native["criteria"] = new JsonObject
                {
                    ["true"] = criteria.True,
                    ["false"] = criteria.False
                };
            }
            else if (question is OpenAIDecisionChoiceQuestion choice)
            {
                if (choice.Choices.Count is < 2 or > 10)
                    throw new ArgumentException("MeraGPT choice questions require 2 to 10 options.", nameof(questions));
                var mappedChoices = new JsonObject();
                foreach (var option in choice.Choices)
                {
                    if (option.Value.Value.ValueKind != JsonValueKind.String)
                        throw new NotSupportedException("MeraGPT Decisions choices must use string labels.");
                    var label = option.Value.Value.GetString()!;
                    if (mappedChoices.ContainsKey(label))
                        throw new ArgumentException("MeraGPT choice labels must be unique.", nameof(questions));
                    mappedChoices[label] = JsonValue.Create(option.Description ?? label);
                }
                native["criteria"] = mappedChoices;
            }
            else if (question is OpenAIDecisionScoreQuestion score)
            {
                if (score.Levels.Count is < 2 or > 10)
                    throw new ArgumentException("MeraGPT score questions require 2 to 10 levels.", nameof(questions));
                native["criteria"] = new JsonArray(score.Levels
                    .Select(level => (JsonNode?)JsonValue.Create(level.Description ?? level.Label))
                    .ToArray());
            }

            result[name] = native;
        }
        return result;
    }

    private OpenAIDecisionResponse MapDecisionResponse(
        JsonElement body,
        OpenAIDecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object
            || body.GetProperty("model").GetString() != "decider-1")
            throw new JsonException("MeraGPT returned a Decisions response for an unexpected model.");

        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count)
            throw new JsonException("MeraGPT Decisions must return one answer per question.");

        var answers = new List<OpenAIDecisionAnswer>(request.Questions.Count);
        foreach (var question in request.Questions)
        {
            var name = question.Name!;
            var answer = nativeAnswers.GetProperty(name);
            var type = answer.GetProperty("type").GetString();
            switch (question)
            {
                case OpenAIDecisionPredicateQuestion when type == "noul":
                    answers.Add(new OpenAIDecisionPredicateAnswer
                    {
                        Name = name,
                        Probability = ReadProbability(answer.GetProperty("noul"))
                    });
                    break;
                case OpenAIDecisionChoiceQuestion choice when type == "choice":
                    answers.Add(MapChoiceAnswer(name, answer, choice));
                    break;
                case OpenAIDecisionScoreQuestion score when type == "score":
                    answers.Add(MapScoreAnswer(name, answer, score));
                    break;
                default:
                    throw new JsonException("MeraGPT Decisions answer type does not match the question.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = ReadUsageCount(usage, "input_tokens");
        var outputTokens = ReadUsageCount(usage, "output_tokens");
        if (inputTokens is null || outputTokens is null)
            throw new JsonException("MeraGPT Decisions response is missing token usage.");

        return new OpenAIDecisionResponse
        {
            Model = "decider-1",
            Answers = answers,
            Usage = new OpenAIDecisionUsage
            {
                InputTokens = inputTokens.Value,
                OutputTokens = outputTokens.Value,
                TotalTokens = checked(inputTokens.Value + outputTokens.Value)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(
                    GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                    DecisionJsonOptions)
            }
        };
    }

    private static OpenAIDecisionChoiceAnswer MapChoiceAnswer(
        string name,
        JsonElement answer,
        OpenAIDecisionChoiceQuestion question)
    {
        if (question.Choices.Count is < 2 or > 10)
            throw new ArgumentException("MeraGPT choice questions require 2 to 10 options.");

        var probabilities = ReadProbabilityMap(
            answer.GetProperty("probabilities"),
            question.Choices.Select(choice => choice.Value.Value.GetString()!));
        var selectedLabel = answer.GetProperty("choice").GetString()
            ?? throw new JsonException("MeraGPT choice answer is missing its selected label.");
        if (!probabilities.TryGetValue(selectedLabel, out var selectedProbability)
            || selectedProbability + 1e-6 < probabilities.Values.Max())
            throw new JsonException("MeraGPT selected choice must have a maximal probability.");

        var confidence = ReadProbability(answer.GetProperty("confidence"));
        if (Math.Abs(confidence - selectedProbability) > 0.000001)
            throw new JsonException("MeraGPT choice confidence must match the selected option probability.");
        return new OpenAIDecisionChoiceAnswer
        {
            Name = name,
            Choice = question.Choices.Single(choice => choice.Value.Value.GetString() == selectedLabel).Value,
            Confidence = confidence,
            Probabilities = question.Choices.Select(choice => new OpenAIDecisionChoiceProbability
            {
                Value = choice.Value,
                Probability = probabilities[choice.Value.Value.GetString()!]
            }).ToArray()
        };
    }

    private static OpenAIDecisionScoreAnswer MapScoreAnswer(
        string name,
        JsonElement answer,
        OpenAIDecisionScoreQuestion question)
    {
        if (question.Levels.Count is < 2 or > 10)
            throw new ArgumentException("MeraGPT score questions require 2 to 10 levels.");

        var expectedKeys = Enumerable.Range(0, question.Levels.Count)
            .Select(index => index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var probabilities = ReadProbabilityMap(answer.GetProperty("probabilities"), expectedKeys);
        var legend = answer.GetProperty("legend");
        if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != question.Levels.Count)
            throw new JsonException("MeraGPT score answer is missing its full legend.");

        var values = new List<OpenAIDecisionScoreProbability>(question.Levels.Count);
        for (var index = 0; index < question.Levels.Count; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            if (!legend.TryGetProperty(key, out var legendValue)
                || legendValue.GetString() != (question.Levels[index].Description ?? question.Levels[index].Label))
                throw new JsonException("MeraGPT score legend does not match the supplied levels.");
            values.Add(new OpenAIDecisionScoreProbability
            {
                Label = question.Levels[index].Label,
                Value = index / (double)(question.Levels.Count - 1),
                Probability = probabilities[key]
            });
        }

        var score = ReadFiniteNumber(answer.GetProperty("score"));
        var weightedScore = values.Sum(item => item.Value * item.Probability);
        if (score < 0 || score > question.Levels.Count - 1
            || Math.Abs(score / (question.Levels.Count - 1) - weightedScore) > 0.000001)
            throw new JsonException("MeraGPT returned an invalid weighted score.");

        var confidence = ReadProbability(answer.GetProperty("confidence"));
        if (Math.Abs(confidence - probabilities.Values.Max()) > 0.000001)
            throw new JsonException("MeraGPT score confidence must match the top-level probability.");

        return new OpenAIDecisionScoreAnswer
        {
            Name = name,
            Score = score / (question.Levels.Count - 1),
            Confidence = confidence,
            Probabilities = values
        };
    }

    private static Dictionary<string, double> ReadProbabilityMap(
        JsonElement value,
        IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != expected.Count)
            throw new JsonException("MeraGPT probability distribution does not cover every option.");

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !probabilities.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("MeraGPT probability distribution contains an unknown or duplicate option.");
        if (Math.Abs(probabilities.Values.Sum() - 1) > 0.000001)
            throw new JsonException("MeraGPT probabilities must sum to one.");
        return probabilities;
    }

    private static double ReadProbability(JsonElement value)
    {
        var number = ReadFiniteNumber(value);
        if (number is < 0 or > 1)
            throw new JsonException("MeraGPT probabilities must be in [0, 1].");
        return number;
    }

    private static double ReadFiniteNumber(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number)
                ? number
                : throw new JsonException("MeraGPT returned an invalid number.");

    private static void ValidateDecisionQuestionNames(IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < questions.Count; index++)
        {
            var name = questions[index].Name ?? "question-" + index.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name == "__proto__" || !names.Add(name))
                throw new ArgumentException("MeraGPT question names must be unique and contain 1 to 128 characters.");
            questions[index].Name = name;
        }
    }

    private static string DescribeLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;

    private static string RenderInput(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();
}
