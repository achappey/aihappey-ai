using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BespokeLabs;

public sealed partial class BespokeLabsProvider
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxQuestions = 64;
    private const int MaxCriteria = 255;
    private const string SystemOneEndpoint = "v1/systemone";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateUnsupportedOpenAIFields(request);

        var input = JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions);
        if (request.Input.Messages?.Any(message =>
                message.Content.Parts?.Any(part => part is not OpenAIDecisionInputText) == true) == true)
            throw new NotSupportedException("BespokeLabs Decisions supports text and JSON state, not image input.");

        var (payload, mappings) = CreatePayload(
            request.Model, input, request.Questions, new HashSet<string>(StringComparer.Ordinal));
        var result = await SendDecisionRequestAsync(payload, mappings, null, cancellationToken);
        return result.Response;
    }

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.State);
        var originalStateFormat = request.StateFormat;
        if (originalStateFormat == "sdk" && HasSdkImagePart(request.State.Value))
            throw new NotSupportedException("BespokeLabs Decisions supports text and JSON state, not image input.");

        request = request.PrepareForProvider(GetIdentifier(), requiresInstructions: false);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new NotSupportedException("BespokeLabs Decisions does not support provider options.");

        var sdkQuestions = CreateSdkQuestions(request.Questions);
        var boolQuestionIds = request.Questions
            .Where(item => item.Value is DecisionBooleanQuestion)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var questionInstructions = request.Questions.ToDictionary(
            item => item.Key,
            item => RenderDecisionText(item.Value.Instructions),
            StringComparer.Ordinal);
        var (payload, mappings) = CreatePayload(
            request.Model, request.State.Value, sdkQuestions, boolQuestionIds,
            request.Questions.Keys.ToArray(), questionInstructions);
        var result = await SendDecisionRequestAsync(
            payload, mappings, request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);
        var response = result.ToDecisionResponse(request, GetIdentifier());
        response.Response!.Id = result.Headers.TryGetValue("x-request-id", out var requestId) ? requestId : null;
        return response;
    }

    private static bool HasSdkImagePart(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var part in state.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "file"
                || !part.TryGetProperty("mediaType", out var mediaType)
                || mediaType.ValueKind != JsonValueKind.String
                || !mediaType.GetString()!.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;
            return true;
        }
        return false;
    }

    private static void ValidateUnsupportedOpenAIFields(OpenAIDecisionRequest request)
    {
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("BespokeLabs Decisions does not support safety_identifier or additional request fields.");
    }

    private static (JsonObject Payload, IReadOnlyList<QuestionMapping> Mappings) CreatePayload(
        string model,
        JsonElement state,
        IReadOnlyList<OpenAIDecisionQuestion> questions,
        IReadOnlySet<string> booleanQuestionIds,
        IReadOnlyList<string>? questionIds = null,
        IReadOnlyDictionary<string, string?>? questionInstructions = null)
    {
        var nativeModel = NormalizeModel(model);
        if (state.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("BespokeLabs Decisions state must be a string, object, or array.", nameof(state));
        if (questions.Count is < 1 or > MaxQuestions)
            throw new ArgumentException("BespokeLabs Decisions accepts between 1 and 64 questions.", nameof(questions));

        var nativeQuestions = new JsonObject();
        var mappings = new List<QuestionMapping>(questions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            var id = questionIds is null ? $"question-{index}" : questionIds[index];
            if (!ids.Add(id))
                throw new ArgumentException("BespokeLabs Decisions requires unique question IDs.", nameof(questions));
            var isBoolean = booleanQuestionIds.Contains(id);
            var instructions = questionInstructions is not null && questionInstructions.TryGetValue(id, out var overrideInstructions)
                ? overrideInstructions
                : question.Instructions;
            var (nativeQuestion, mapping) = MapQuestion(id, question, isBoolean, instructions);
            nativeQuestions[id] = nativeQuestion;
            mappings.Add(mapping);
        }

        var payload = new JsonObject
        {
            ["model"] = nativeModel,
            ["state"] = JsonNode.Parse(state.GetRawText()),
            ["questions"] = nativeQuestions
        };
        if (Encoding.UTF8.GetByteCount(payload.ToJsonString()) > MaxRequestBytes)
            throw new ArgumentException("BespokeLabs Decisions request exceeds the 1 MiB body limit.", nameof(state));

        return (payload, mappings);
    }

    private static (JsonObject NativeQuestion, QuestionMapping Mapping) MapQuestion(
        string id, OpenAIDecisionQuestion question, bool isBoolean, string? instructions)
    {
        switch (question)
        {
            case OpenAIDecisionPredicateQuestion:
                return (CreateNativeQuestion("noul", instructions),
                    new(id, question.Name, QuestionKind.Predicate, [], []));

            case OpenAIDecisionChoiceQuestion choice when isBoolean:
            {
                var trueChoice = choice.Choices.SingleOrDefault(item => item.Value.Value.ValueKind == JsonValueKind.True)
                    ?? throw new ArgumentException($"Boolean question '{id}' must include true and false criteria.");
                var falseChoice = choice.Choices.SingleOrDefault(item => item.Value.Value.ValueKind == JsonValueKind.False)
                    ?? throw new ArgumentException($"Boolean question '{id}' must include true and false criteria.");
                if (choice.Choices.Count != 2)
                    throw new ArgumentException($"Boolean question '{id}' must include only true and false criteria.");
                var criteria = new JsonObject();
                if (trueChoice.Description is not null)
                    criteria["true"] = trueChoice.Description;
                if (falseChoice.Description is not null)
                    criteria["false"] = falseChoice.Description;
                var native = CreateNativeQuestion("noul", instructions);
                if (criteria.Count > 0)
                    native["criteria"] = criteria;
                return (native, new(id, question.Name, QuestionKind.BooleanNoul, [trueChoice, falseChoice], []));
            }

            case OpenAIDecisionChoiceQuestion choice:
            {
                if (choice.Choices.Count is < 2 or > MaxCriteria)
                    throw new ArgumentException($"Choice question '{id}' requires between 2 and 255 choices.");
                var criteria = new JsonObject();
                var choiceMappings = new List<OpenAIDecisionChoice>(choice.Choices.Count);
                for (var index = 0; index < choice.Choices.Count; index++)
                {
                    var key = $"option-{index}";
                    var item = choice.Choices[index];
                    criteria[key] = item.Description;
                    choiceMappings.Add(item);
                }
                var native = CreateNativeQuestion("choice", instructions);
                native["criteria"] = criteria;
                return (native, new(id, question.Name, QuestionKind.Choice, choiceMappings, []));
            }

            case OpenAIDecisionScoreQuestion score:
            {
                if (score.Levels.Count is < 2 or > MaxCriteria)
                    throw new ArgumentException($"Score question '{id}' requires between 2 and 255 levels.");
                var criteria = new JsonArray();
                foreach (var level in score.Levels)
                    criteria.Add(level.Description ?? level.Label);
                var native = CreateNativeQuestion("score", instructions);
                native["criteria"] = criteria;
                return (native, new(id, question.Name, QuestionKind.Score, [], score.Levels));
            }

            default:
                throw new ArgumentException($"Question '{id}' has an unsupported type.", nameof(question));
        }
    }

    private static JsonObject CreateNativeQuestion(string type, string? instructions)
    {
        var question = new JsonObject { ["type"] = type };
        if (instructions is not null)
            question["instructions"] = instructions;
        return question;
    }

    private static IReadOnlyList<OpenAIDecisionQuestion> CreateSdkQuestions(
        IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var result = new List<OpenAIDecisionQuestion>(questions.Count);
        foreach (var (id, question) in questions)
        {
            OpenAIDecisionQuestion mapped = question switch
            {
                DecisionBooleanQuestion { Criteria: null } => new OpenAIDecisionPredicateQuestion(),
                DecisionBooleanQuestion boolean => new OpenAIDecisionChoiceQuestion
                {
                    Choices =
                    [
                        new() { Value = true, Description = RenderDecisionText(boolean.Criteria!.True) },
                        new() { Value = false, Description = RenderDecisionText(boolean.Criteria.False) }
                    ]
                },
                DecisionChoiceQuestion choice => new OpenAIDecisionChoiceQuestion
                {
                    Choices = choice.Criteria.Select(item => new OpenAIDecisionChoice
                    {
                        Value = item.Key, Description = RenderDecisionText(item.Value)
                    }).ToArray()
                },
                DecisionScoreQuestion score => new OpenAIDecisionScoreQuestion
                {
                    Levels = score.Criteria.Select((criteria, index) => new OpenAIDecisionScoreLevel
                    {
                        Label = index.ToString(CultureInfo.InvariantCulture),
                        Description = RenderDecisionText(criteria)
                    }).ToArray()
                },
                _ => throw new ArgumentException($"Question '{id}' has an unsupported type.", nameof(questions))
            };
            mapped.Name = id;
            mapped.Instructions = string.Empty;
            result.Add(mapped);
        }
        return result;
    }

    private static string? RenderDecisionText(DecisionInput? input)
        => input is null
            ? null
            : input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();

    private static string NormalizeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        const string prefix = "bespokelabs/";
        var nativeModel = model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
        if (nativeModel is not ("nimble-latest" or "nimble-v4-max" or "nimble-v4" or "nimble-v3"))
            throw new NotSupportedException($"BespokeLabs Decisions does not support model '{model}'.");
        return nativeModel;
    }

    private async Task<OpenAICompatibleDecisionResult> SendDecisionRequestAsync(
        JsonObject payload,
        IReadOnlyList<QuestionMapping> mappings,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        var body = payload.ToJsonString();
        var response = await SendAsync(SystemOneEndpoint, body, headers, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            response = await SendAsync("v1/nimble/systemone", body, headers, cancellationToken);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var responseHeaders = response.GetHeaders();
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"BespokeLabs System One request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                    null, response.StatusCode);

            try
            {
                using var document = JsonDocument.Parse(raw);
                var nativeBody = document.RootElement;
                var mapped = MapResponse(nativeBody, payload["model"]!.GetValue<string>(), mappings);
                return new(mapped, responseHeaders, nativeBody.Clone());
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException
                                               or KeyNotFoundException or FormatException or OverflowException)
            {
                throw new InvalidOperationException("BespokeLabs returned an invalid System One response.", exception);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string endpoint,
        string body,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var header in headers ?? [])
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static OpenAIDecisionResponse MapResponse(
        JsonElement body, string expectedModel, IReadOnlyList<QuestionMapping> mappings)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || model.GetString() != expectedModel
            || !body.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object
            || answers.EnumerateObject().Count() != mappings.Count
            || !body.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("System One response must contain the requested model, every answer, and usage.");

        var inputTokens = ReadTokenCount(usage, "input_tokens");
        var outputTokens = ReadTokenCount(usage, "output_tokens");
        var convertedAnswers = new List<OpenAIDecisionAnswer>(mappings.Count);
        foreach (var mapping in mappings)
        {
            if (!answers.TryGetProperty(mapping.Id, out var answer))
                throw new JsonException($"System One response is missing question '{mapping.Id}'.");
            convertedAnswers.Add(MapAnswer(answer, mapping));
        }

        return new()
        {
            Model = expectedModel,
            Answers = convertedAnswers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            }
        };
    }

    private static OpenAIDecisionAnswer MapAnswer(JsonElement answer, QuestionMapping mapping)
    {
        if (answer.ValueKind != JsonValueKind.Object
            || !answer.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new JsonException($"System One answer '{mapping.Id}' is missing its type.");

        var answerType = type.GetString();
        if (mapping.Kind is QuestionKind.Predicate or QuestionKind.BooleanNoul)
        {
            if (answerType != "noul")
                throw new JsonException($"System One answer '{mapping.Id}' has the wrong type.");
            var probability = ReadProbability(answer, "noul");
            if (mapping.Kind == QuestionKind.Predicate)
                return new OpenAIDecisionPredicateAnswer { Name = mapping.Name, Probability = probability };

            var trueProbability = probability;
            var falseProbability = 1 - probability;
            var choice = trueProbability >= falseProbability ? mapping.Choices[0].Value : mapping.Choices[1].Value;
            return new OpenAIDecisionChoiceAnswer
            {
                Name = mapping.Name,
                Choice = choice,
                Confidence = Math.Max(trueProbability, falseProbability),
                Probabilities =
                [
                    new() { Value = mapping.Choices[0].Value, Probability = trueProbability },
                    new() { Value = mapping.Choices[1].Value, Probability = falseProbability }
                ]
            };
        }

        if (answerType != (mapping.Kind == QuestionKind.Choice ? "choice" : "score"))
            throw new JsonException($"System One answer '{mapping.Id}' has the wrong type.");

        if (mapping.Kind == QuestionKind.Choice)
            return MapChoiceAnswer(answer, mapping);
        return MapScoreAnswer(answer, mapping);
    }

    private static OpenAIDecisionChoiceAnswer MapChoiceAnswer(JsonElement answer, QuestionMapping mapping)
    {
        var probabilities = ReadDistribution(answer, "probabilities", mapping.Choices.Count);
        var selected = ReadString(answer, "choice");
        var confidence = ReadProbability(answer, "confidence");
        var choices = mapping.Choices.Select((item, index) => (Key: $"option-{index}", Item: item)).ToArray();
        var choice = choices.SingleOrDefault(item => item.Key == selected);
        if (choice.Item is null || !probabilities.TryGetValue(selected, out var selectedProbability)
            || selectedProbability + 1e-6 < probabilities.Values.Max())
            throw new JsonException("System One selected a choice that is not a most-probable option.");

        return new()
        {
            Name = mapping.Name,
            Choice = choice.Item.Value,
            Confidence = confidence,
            Probabilities = choices.Select(item => new OpenAIDecisionChoiceProbability
            {
                Value = item.Item.Value,
                Probability = probabilities[item.Key]
            }).ToArray()
        };
    }

    private static OpenAIDecisionScoreAnswer MapScoreAnswer(JsonElement answer, QuestionMapping mapping)
    {
        var count = mapping.Levels.Count;
        var probabilities = ReadDistribution(answer, "probabilities", count);
        var score = ReadNumber(answer, "score");
        var confidence = ReadProbability(answer, "confidence");
        if (score < 0 || score > count - 1
            || Math.Abs(score - probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value)) > 1e-5)
            throw new JsonException("System One score does not match its probability distribution.");

        var legend = answer.GetProperty("legend");
        if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != count)
            throw new JsonException("System One score legend must cover every level.");
        var result = new OpenAIDecisionScoreProbability[count];
        for (var index = 0; index < count; index++)
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            if (!legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String
                || !probabilities.TryGetValue(key, out var probability))
                throw new JsonException("System One score legend or distribution contains an unknown level.");
            result[index] = new()
            {
                Label = mapping.Levels[index].Label,
                Value = index,
                Probability = probability
            };
        }

        return new()
        {
            Name = mapping.Name,
            Score = score,
            Confidence = confidence,
            Probabilities = result
        };
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement answer, string propertyName, int expectedCount)
    {
        if (!answer.TryGetProperty(propertyName, out var distribution) || distribution.ValueKind != JsonValueKind.Object
            || distribution.EnumerateObject().Count() != expectedCount)
            throw new JsonException("System One answer must provide a probability for every option.");

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in distribution.EnumerateObject())
        {
            var probability = ReadNumber(item.Value);
            if (probability is < 0 or > 1 || !result.TryAdd(item.Name, probability))
                throw new JsonException("System One probabilities must be unique values in [0, 1].");
        }
        if (Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("System One probabilities must sum to one.");
        return result;
    }

    private static int ReadTokenCount(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value) || !value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"System One usage requires a nonnegative integer '{name}'.");
        return count;
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new JsonException($"System One answer requires string '{name}'.");
        return value.GetString()!;
    }

    private static double ReadProbability(JsonElement root, string name)
    {
        var probability = ReadNumber(root, name);
        if (probability is < 0 or > 1)
            throw new JsonException($"System One probability '{name}' must be in [0, 1].");
        return probability;
    }

    private static double ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new JsonException($"System One answer requires number '{name}'.");
        return ReadNumber(value);
    }

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("System One response contains a non-finite number.");
        return number;
    }

    private enum QuestionKind { Predicate, BooleanNoul, Choice, Score }

    private sealed record QuestionMapping(
        string Id,
        string? Name,
        QuestionKind Kind,
        IReadOnlyList<OpenAIDecisionChoice> Choices,
        IReadOnlyList<OpenAIDecisionScoreLevel> Levels);
}
