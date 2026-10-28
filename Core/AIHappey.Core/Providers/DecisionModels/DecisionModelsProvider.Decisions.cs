using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DecisionModels;

public sealed partial class DecisionModelsProvider
{
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record QuestionMapping(
        string Id,
        OpenAIDecisionQuestion Source,
        IReadOnlyList<string>? OptionIds = null);

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = DecisionRequestCompatibility.PrepareForProvider(request, GetIdentifier());
        var openAIRequest = request.ToOpenAIDecisionRequest(GetIdentifier());
        var region = ReadRegion(request.ProviderOptions);
        openAIRequest.AdditionalProperties?.Remove(GetIdentifier());
        var nativeBooleanCriteria = new Dictionary<int, DecisionBooleanCriteria>();
        var questions = openAIRequest.Questions.ToArray();
        var index = 0;
        foreach (var question in request.Questions.Values)
        {
            if (question is DecisionBooleanQuestion { Criteria: not null } boolean)
            {
                nativeBooleanCriteria[index] = boolean.Criteria;
                questions[index] = new OpenAIDecisionPredicateQuestion
                {
                    Name = questions[index].Name,
                    Instructions = questions[index].Instructions
                };
            }
            index++;
        }
        openAIRequest.Questions = questions;
        var headers = request.Headers?.GetProviderPassthroughHeaders(GetIdentifier());
        var result = await ExecuteOpenAIDecisionRequestAsync(
            openAIRequest, region, headers, nativeBooleanCriteria, cancellationToken);
        var answers = result.Response.Answers.ToArray();
        for (var questionIndex = 0; questionIndex < request.Questions.Count; questionIndex++)
        {
            if (!nativeBooleanCriteria.ContainsKey(questionIndex))
                continue;
            var predicate = (OpenAIDecisionPredicateAnswer)answers[questionIndex];
            answers[questionIndex] = new OpenAIDecisionChoiceAnswer
            {
                Name = predicate.Name,
                Choice = true,
                Confidence = Math.Max(predicate.Probability, 1 - predicate.Probability),
                Probabilities =
                [
                    new() { Value = true, Probability = predicate.Probability },
                    new() { Value = false, Probability = 1 - predicate.Probability }
                ]
            };
        }
        result.Response.Answers = answers;
        return result.ToDecisionResponse(request, GetIdentifier());
    }

    private async Task<OpenAICompatibleDecisionResult> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        string? configuredRegion,
        Dictionary<string, string>? passthroughHeaders,
        IReadOnlyDictionary<int, DecisionBooleanCriteria>? nativeBooleanCriteria,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);

        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("Decision Models Decisions does not support safety_identifier.");

        var region = configuredRegion ?? ReadOpenAIRegion(request.AdditionalProperties);
        var additionalProperties = request.AdditionalProperties;
        if (additionalProperties is { Count: > 0 }
            && (additionalProperties.Count != 1 || !additionalProperties.ContainsKey("providerOptions")))
            throw new ArgumentException("Decision Models Decisions does not support additional OpenAI request fields.", nameof(request));

        var model = RemoveProviderPrefix(request.Model);
        ValidateRequestLimits(model, request.Questions);
        var (state, images) = MapInput(request.Input);
        if (string.IsNullOrWhiteSpace(state))
            throw new ArgumentException("Decision Models requires nonempty text state.", nameof(request));
        ValidateImages(model, images);
        ValidatePayloadSize(model, state);

        var questions = MapQuestions(request.Questions);
        var body = new JsonObject
        {
            ["model"] = model,
            ["state"] = state,
            ["questions"] = MapNativeQuestions(questions, nativeBooleanCriteria)
        };
        if (images.Count == 1)
            body["images"] = new JsonArray(JsonValue.Create(images[0]));

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            images.Count == 1 ? "v1/multimodal" : "v1/systemone")
        {
            Content = new StringContent(body.ToJsonString(DecisionJsonOptions), Encoding.UTF8, "application/json")
        };

        foreach (var (name, value) in passthroughHeaders ?? [])
        {
            if (name.Equals("S1-Region", StringComparison.OrdinalIgnoreCase))
            {
                var headerRegion = NormalizeRegion(value);
                if (region is not null && headerRegion is not null && region != headerRegion)
                    throw new ArgumentException("S1-Region header conflicts with providerOptions.decisionmodels.region.");
                region ??= headerRegion;
                continue;
            }
            message.Headers.TryAddWithoutValidation(name, value);
        }
        if (region is not null)
            message.Headers.TryAddWithoutValidation("S1-Region", region);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseHeaders = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Decision Models decision request failed ({(int)response.StatusCode} {response.ReasonPhrase})"
                + (responseHeaders.TryGetValue("x-request-id", out var requestId) ? $" [request {requestId}]" : "")
                + (responseHeaders.TryGetValue("retry-after", out var retryAfter) ? $" [Retry-After: {retryAfter}]" : "")
                + $": {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var mapped = MapResponse(root, questions);
            var decisionResponse = new OpenAIDecisionResponse
            {
                Model = root.GetProperty("model").GetString()!,
                Answers = mapped,
                Usage = new()
                {
                    InputTokens = ReadUsageCount(root, "input_tokens"),
                    OutputTokens = ReadUsageCount(root, "output_tokens"),
                    TotalTokens = checked(ReadUsageCount(root, "input_tokens") + ReadUsageCount(root, "output_tokens"))
                },
                AdditionalProperties = new()
                {
                    ["providerMetadata"] = JsonSerializer.SerializeToElement(
                        GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()), DecisionJsonOptions)
                }
            };
            return new OpenAICompatibleDecisionResult(decisionResponse, responseHeaders, root.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Decision Models returned an invalid Decisions response.", exception);
        }
    }

    private static List<QuestionMapping> MapQuestions(IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        var result = new List<QuestionMapping>(questions.Count);
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
            var options = question is OpenAIDecisionChoiceQuestion choice
                ? MapChoiceIds(choice.Choices)
                : null;
            result.Add(new QuestionMapping(id, question, options));
        }
        return result;
    }

    private static JsonObject MapNativeQuestions(
        IReadOnlyList<QuestionMapping> questions,
        IReadOnlyDictionary<int, DecisionBooleanCriteria>? nativeBooleanCriteria)
    {
        var result = new JsonObject();
        for (var index = 0; index < questions.Count; index++)
        {
            var mapping = questions[index];
            var question = mapping.Source;
            var native = new JsonObject
            {
                ["type"] = question switch
                {
                    OpenAIDecisionPredicateQuestion => "noul",
                    OpenAIDecisionChoiceQuestion => "choice",
                    OpenAIDecisionScoreQuestion => "score",
                    _ => throw new NotSupportedException($"Unsupported Decisions question type '{question.Type}'.")
                },
                ["instructions"] = question.Instructions
            };

            switch (question)
            {
                case OpenAIDecisionChoiceQuestion choice:
                    var criteria = new JsonObject();
                    for (var index = 0; index < choice.Choices.Count; index++)
                        criteria[mapping.OptionIds![index]] = choice.Choices[index].Description;
                    native["criteria"] = criteria;
                    break;
                case OpenAIDecisionScoreQuestion score:
                    native["criteria"] = new JsonArray(score.Levels
                        .Select(level => (JsonNode?)JsonValue.Create(DescribeScoreLevel(level)))
                        .ToArray());
                    break;
            }
            if (nativeBooleanCriteria?.TryGetValue(index, out var booleanCriteria) == true)
                native["criteria"] = JsonSerializer.SerializeToNode(booleanCriteria, DecisionJsonOptions);
            result[mapping.Id] = native;
        }
        return result;
    }

    private static IReadOnlyList<OpenAIDecisionAnswer> MapResponse(
        JsonElement root,
        IReadOnlyList<QuestionMapping> questions)
    {
        if (root.GetProperty("model").ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(root.GetProperty("model").GetString()))
            throw new JsonException("Decision response must include a model.");

        var answers = root.GetProperty("answers");
        if (answers.ValueKind != JsonValueKind.Object || answers.EnumerateObject().Count() != questions.Count)
            throw new JsonException("Decision answers must cover every question exactly once.");

        var result = new List<OpenAIDecisionAnswer>(questions.Count);
        foreach (var mapping in questions)
        {
            var answer = answers.GetProperty(mapping.Id);
            var type = answer.GetProperty("type").GetString();
            OpenAIDecisionAnswer mapped = (mapping.Source, type) switch
            {
                (OpenAIDecisionPredicateQuestion, "noul") => new OpenAIDecisionPredicateAnswer
                {
                    Probability = ReadProbability(answer.GetProperty("noul"))
                },
                (OpenAIDecisionChoiceQuestion choice, "choice") => MapChoiceAnswer(answer, choice, mapping.OptionIds!),
                (OpenAIDecisionScoreQuestion score, "score") => MapScoreAnswer(answer, score),
                _ => throw new JsonException("Decision answer type does not match the submitted question.")
            };
            mapped.Name = mapping.Source.Name;
            result.Add(mapped);
        }

        _ = ReadUsageCount(root, "input_tokens");
        _ = ReadUsageCount(root, "output_tokens");
        return result;
    }

    private static OpenAIDecisionChoiceAnswer MapChoiceAnswer(
        JsonElement answer,
        OpenAIDecisionChoiceQuestion question,
        IReadOnlyList<string> optionIds)
    {
        var confidence = ReadProbability(answer.GetProperty("confidence"));
        var nativeChoice = answer.GetProperty("choice").GetString()
            ?? throw new JsonException("Decision choice answer must be a string.");
        var selectedIndex = Array.IndexOf(optionIds.ToArray(), nativeChoice);
        if (selectedIndex < 0)
            throw new JsonException("Decision choice answer selected an unknown option.");

        var probabilities = ReadDistribution(answer.GetProperty("probabilities"), optionIds);
        return new OpenAIDecisionChoiceAnswer
        {
            Choice = question.Choices[selectedIndex].Value,
            Confidence = confidence,
            Probabilities = question.Choices.Select((choice, index) => new OpenAIDecisionChoiceProbability
            {
                Value = choice.Value,
                Probability = probabilities[optionIds[index]]
            }).ToArray()
        };
    }

    private static OpenAIDecisionScoreAnswer MapScoreAnswer(JsonElement answer, OpenAIDecisionScoreQuestion question)
    {
        var confidence = ReadProbability(answer.GetProperty("confidence"));
        var keys = Enumerable.Range(0, question.Levels.Count)
            .Select(index => index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var probabilitiesElement = answer.GetProperty("probabilities");
        var probabilities = ReadDistribution(probabilitiesElement, keys);
        var score = answer.GetProperty("score").GetDouble();
        if (!double.IsFinite(score) || score < 0 || score > question.Levels.Count - 1)
            throw new JsonException("Decision score must be within its submitted rubric.");

        var levels = question.Levels.Select((level, index) => new OpenAIDecisionScoreProbability
        {
            Label = index.ToString(CultureInfo.InvariantCulture),
            Value = index,
            Probability = probabilities[keys[index]]
        }).ToArray();
        return new OpenAIDecisionScoreAnswer
        {
            Score = score,
            Confidence = confidence,
            Probabilities = levels
        };
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision probabilities must be an object.");
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Decision probabilities contain an unknown or duplicate option.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Decision probabilities must cover all options and sum to one.");
        return result;
    }

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Decision probability must be finite and in [0, 1].");
        return number;
    }

    private static int ReadUsageCount(JsonElement root, string property)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision response must include usage.");
        if (!usage.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var result) || result < 0)
            throw new JsonException($"Decision usage.{property} must be a nonnegative integer.");
        return result;
    }

    private static (string State, List<string> Images) MapInput(OpenAIDecisionInput input)
    {
        var text = new List<string>();
        var images = new List<string>();
        if (input.Text is not null)
            text.Add(input.Text);
        else
            foreach (var message in input.Messages!)
            {
                if (message.Content.Text is not null)
                    text.Add(message.Content.Text);
                else
                    foreach (var part in message.Content.Parts!)
                        switch (part)
                        {
                            case OpenAIDecisionInputText inputText:
                                text.Add(inputText.Text);
                                break;
                            case OpenAIDecisionInputImage { Detail: not null }:
                                throw new NotSupportedException("Decision Models does not document image detail controls.");
                            case OpenAIDecisionInputImage image:
                                images.Add(image.ImageUrl);
                                break;
                            default:
                                throw new NotSupportedException("Decision Models supports text and image decision input only.");
                        }
            }

        if (images.Count > 0 && text.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Decision Models multimodal requests require text state alongside the image.");
        return (string.Join("\n\n", text), images);
    }

    private static string[] MapChoiceIds(IReadOnlyList<OpenAIDecisionChoice> choices)
    {
        var labels = choices.Select(choice => choice.Value.Value.ValueKind == JsonValueKind.String
            ? choice.Value.Value.GetString()!
            : choice.Value.Value.GetRawText()).ToArray();
        var result = new string[labels.Length];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < labels.Length; index++)
        {
            var label = labels[index];
            if (!used.Add(label))
            {
                var suffix = index;
                do
                {
                    label = OptionId(suffix++);
                }
                while (used.Contains(label) || labels.Contains(label, StringComparer.Ordinal));
                used.Add(label);
            }
            result[index] = label;
        }
        return result;
    }

    private static string DescribeScoreLevel(OpenAIDecisionScoreLevel level)
        => string.IsNullOrWhiteSpace(level.Description) ? level.Label : $"{level.Label}: {level.Description}";

    private static string OptionId(int index) => "option-" + index.ToString(CultureInfo.InvariantCulture);

    private static void ValidateRequestLimits(string model, IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        var maximumQuestions = IsMultiQuestionModel(model) ? 3 : 1;
        if (questions.Count > maximumQuestions)
            throw new ArgumentException($"Decision Models model '{model}' accepts at most {maximumQuestions} question(s).");

        foreach (var question in questions)
        {
            var count = question switch
            {
                OpenAIDecisionChoiceQuestion choice => choice.Choices.Count,
                OpenAIDecisionScoreQuestion score => score.Levels.Count,
                _ => 0
            };
            if (question is OpenAIDecisionChoiceQuestion or OpenAIDecisionScoreQuestion && count < 2)
                throw new ArgumentException($"Decision Models model '{model}' requires at least two options per choice or score question.");
            var maxOptions = GetMaximumOptions(model);
            if (maxOptions is not null && count > maxOptions.Value)
                throw new ArgumentException($"Decision Models model '{model}' accepts at most {maxOptions.Value} options per question.");
        }
    }

    private static bool IsMultiQuestionModel(string model)
        => model.StartsWith("s1-pro", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("reax-", StringComparison.OrdinalIgnoreCase);

    private static int? GetMaximumOptions(string model)
    {
        if (model.StartsWith("s1-fast", StringComparison.OrdinalIgnoreCase)) return 16;
        if (model.StartsWith("s1-vision", StringComparison.OrdinalIgnoreCase)) return 26;
        if (IsMultiQuestionModel(model)) return 64;
        if (model.Equals("quyet-1-large", StringComparison.OrdinalIgnoreCase)) return 10;
        return null;
    }

    private static void ValidateImages(string model, IReadOnlyCollection<string> images)
    {
        if (images.Count == 0)
            return;
        if (!model.StartsWith("s1-vision", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Decision Models model '{model}' does not document image input.");
        if (images.Count != 1)
            throw new ArgumentException("Decision Models accepts exactly one image per multimodal request.");

        var image = images.Single();
        var comma = image.IndexOf(',');
        var header = comma < 0 ? "" : image[..comma].ToLowerInvariant();
        if (header is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64"))
            throw new ArgumentException("Decision Models images must be PNG, JPEG or WebP base64 data URLs.");
        if ((image.Length - comma - 1) / 4L * 3 > MaxImageBytes)
            throw new ArgumentException("Decision Models images must be at most 4 MiB decoded.");
        try
        {
            if (Convert.FromBase64String(image[(comma + 1)..]).Length is 0 or > MaxImageBytes)
                throw new ArgumentException("Decision Models images must be nonempty and at most 4 MiB decoded.");
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Decision Models image contains invalid base64.", exception);
        }
    }

    private static void ValidatePayloadSize(string model, string state)
    {
        var maximumTokens = GetMaximumInputTokens(model);
        if (maximumTokens is null)
            return;
        var serialized = JsonSerializer.SerializeToUtf8Bytes(state, DecisionJsonOptions);
        if (serialized.Length > maximumTokens.Value * 8L)
            throw new HttpRequestException(
                $"Decision Models state exceeds the documented {maximumTokens.Value * 8L}-byte payload limit for '{model}'.",
                null,
                System.Net.HttpStatusCode.RequestEntityTooLarge);
    }

    private static int? GetMaximumInputTokens(string model)
    {
        if (model.StartsWith("s1-fast", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("s1-vision", StringComparison.OrdinalIgnoreCase)
            || model.Equals("quyet-1-large", StringComparison.OrdinalIgnoreCase))
            return 8000;
        if (IsMultiQuestionModel(model))
            return 8192;
        return null;
    }

    private static string RemoveProviderPrefix(string model)
    {
        var prefix = "decisionmodels/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string? ReadRegion(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("decisionmodels", out var options) != true
            || options.ValueKind == JsonValueKind.Null)
            return null;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.decisionmodels must be an object.");

        string? region = null;
        foreach (var property in options.EnumerateObject())
        {
            if (!property.NameEquals("region"))
                throw new ArgumentException($"Decision Models does not support provider option '{property.Name}'.");
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("providerOptions.decisionmodels.region must be 'global' or 'eu'.");
            region = property.Value.GetString();
        }
        return NormalizeRegion(region);
    }

    private static string? ReadOpenAIRegion(Dictionary<string, JsonElement>? properties)
    {
        if (properties?.TryGetValue("providerOptions", out var options) != true)
            return null;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions must be an object.");
        var providerOptions = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
            providerOptions[property.Name] = property.Value.Clone();
        return ReadRegion(providerOptions);
    }

    private static string? NormalizeRegion(string? region)
        => region?.ToLowerInvariant() switch
        {
            null => null,
            "global" => "global",
            "eu" => "eu",
            _ => throw new ArgumentException("Decision Models region must be 'global' or 'eu'.")
        };
}
