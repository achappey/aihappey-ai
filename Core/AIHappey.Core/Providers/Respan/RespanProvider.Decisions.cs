using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Respan;

public sealed partial class RespanProvider
{
    private const string DecisionsPath = "api/v1/decisions";
    private static readonly JsonSerializerOptions DecisionsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RespanQuestion(
        string Id,
        OpenAIDecisionQuestion Source,
        string Type,
        IReadOnlyList<OpenAIDecisionChoiceValue>? Choices = null,
        IReadOnlyList<OpenAIDecisionScoreLevel>? Levels = null);

    private async Task<OpenAICompatibleDecisionResult> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        JsonElement? rawState,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateDecisionModel(request.Model);

        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("Respan Decisions does not support safety_identifier.");

        var (reasoning, images, respanParams) = ParseDecisionOptions(request.AdditionalProperties);
        var result = await SendDecisionRequestAsync(
            request,
            rawState,
            reasoning,
            images,
            respanParams,
            headers,
            cancellationToken);
        return result;
    }

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());

        var nativeRequest = request.ToOpenAIDecisionRequest(GetIdentifier());
        var result = await ExecuteOpenAIDecisionRequestAsync(
            nativeRequest,
            request.State.Value,
            request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()),
            cancellationToken);

        return result.ToDecisionResponse(request, GetIdentifier());
    }

    private async Task<OpenAICompatibleDecisionResult> SendDecisionRequestAsync(
        OpenAIDecisionRequest request,
        JsonElement? rawState,
        JsonObject? reasoning,
        IReadOnlyList<string> explicitImages,
        JsonObject? respanParams,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var questions = MapQuestions(request.Questions);
        var images = new List<string>(explicitImages);
        var state = MapState(request.Input, rawState, images);
        if (images.Count > 4)
            throw new ArgumentException("Respan span-02 supports at most four images per request.", nameof(request));

        var body = new JsonObject
        {
            ["model"] = "span-02",
            ["state"] = state,
            ["questions"] = MapNativeQuestions(questions)
        };
        if (images.Count > 0)
            body["images"] = JsonSerializer.SerializeToNode(images, DecisionsJsonOptions);
        if (reasoning is not null)
            body["reasoning"] = reasoning;
        if (respanParams is not null)
            body["respan_params"] = respanParams;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, DecisionsPath)
        {
            Content = new StringContent(body.ToJsonString(DecisionsJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var (name, value) in headers ?? [])
            httpRequest.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"Respan Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Respan Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var mapped = MapDecisionResponse(root, questions);
            return new OpenAICompatibleDecisionResult(mapped, response.GetHeaders(), root.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Respan returned an invalid Decisions response.", exception);
        }
    }

    private static List<RespanQuestion> MapQuestions(IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        var result = new List<RespanQuestion>(questions.Count);
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
            var type = question switch
            {
                OpenAIDecisionPredicateQuestion => "noul",
                OpenAIDecisionChoiceQuestion => "choice",
                OpenAIDecisionScoreQuestion => "score",
                _ => throw new NotSupportedException($"Respan Decisions does not support question type '{question.Type}'.")
            };
            result.Add(new RespanQuestion(
                id,
                question,
                type,
                question is OpenAIDecisionChoiceQuestion choice ? choice.Choices.Select(item => item.Value).ToArray() : null,
                question is OpenAIDecisionScoreQuestion score ? score.Levels : null));
        }

        return result;
    }

    private static void ValidateDecisionModel(string model)
    {
        const string prefix = "respan/";
        var nativeModel = model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
        if (!string.Equals(nativeModel, "span-02", StringComparison.Ordinal))
            throw new NotSupportedException($"Respan Decisions does not support model '{model}'.");
    }

    private static JsonObject MapNativeQuestions(IReadOnlyList<RespanQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var item in questions)
        {
            var question = new JsonObject
            {
                ["type"] = item.Type,
                ["instructions"] = item.Source.Instructions
            };

            switch (item.Source)
            {
                case OpenAIDecisionChoiceQuestion choice:
                    var criteria = new JsonObject();
                    for (var index = 0; index < choice.Choices.Count; index++)
                    {
                        var key = ChoiceKey(index);
                        var value = choice.Choices[index];
                        criteria[key] = choice.Choices[index].Description
                            ?? (value.Value.Value.ValueKind switch
                            {
                                JsonValueKind.String => value.Value.Value.GetString(),
                                JsonValueKind.True => "true",
                                JsonValueKind.False => "false",
                                _ => throw new NotSupportedException("Respan Decisions supports string and boolean choice values only.")
                            });
                    }
                    question["criteria"] = criteria;
                    break;
                case OpenAIDecisionScoreQuestion score:
                    var levels = new JsonArray();
                    foreach (var level in score.Levels)
                        levels.Add(level.Description ?? level.Label);
                    question["criteria"] = levels;
                    break;
            }

            result[item.Id] = question;
        }

        return result;
    }

    private static OpenAIDecisionResponse MapDecisionResponse(
        JsonElement root,
        IReadOnlyList<RespanQuestion> questions)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !root.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object
            || answers.EnumerateObject().Count() != questions.Count)
            throw new JsonException("Respan Decisions response requires a model and one answer per question.");

        var mappedAnswers = new List<OpenAIDecisionAnswer>(questions.Count);
        foreach (var question in questions)
        {
            if (!answers.TryGetProperty(question.Id, out var answer) || answer.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Respan Decisions response is missing answer '{question.Id}'.");

            var answerType = ReadRequiredString(answer, "type");
            OpenAIDecisionAnswer mapped = (question, answerType) switch
            {
                (_, "refusal") => new OpenAIDecisionRefusalAnswer(),
                ({ Type: "noul" }, "noul") => new OpenAIDecisionPredicateAnswer
                {
                    Probability = ReadProbability(answer, "noul")
                },
                ({ Type: "choice", Choices: { } choices }, "choice") => MapChoiceAnswer(answer, choices),
                ({ Type: "score", Levels: { } levels }, "score") => MapScoreAnswer(answer, levels),
                _ => throw new JsonException($"Respan returned an unsupported answer type '{answerType}' for '{question.Id}'.")
            };
            mapped.Name = question.Source.Name;
            mappedAnswers.Add(mapped);
        }

        var usage = ReadUsage(root);
        var additionalProperties = root.EnumerateObject()
            .Where(item => item.Name is not ("model" or "answers" or "usage"))
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);

        return new OpenAIDecisionResponse
        {
            Model = model.GetString()!,
            Answers = mappedAnswers,
            Usage = usage,
            AdditionalProperties = additionalProperties.Count == 0 ? null : additionalProperties
        };
    }

    private static OpenAIDecisionChoiceAnswer MapChoiceAnswer(
        JsonElement answer,
        IReadOnlyList<OpenAIDecisionChoiceValue> choices)
    {
        var selectedKey = ReadRequiredString(answer, "choice");
        var selectedIndex = ParseChoiceKey(selectedKey, choices.Count);
        var nativeProbabilities = ReadProbabilityObject(answer, "probabilities", choices.Count);
        var probabilities = choices.Select((choice, index) =>
        {
            var key = ChoiceKey(index);
            if (!nativeProbabilities.TryGetValue(key, out var probability))
                throw new JsonException($"Respan choice probabilities are missing '{key}'.");
            return new OpenAIDecisionChoiceProbability { Value = choice, Probability = probability };
        }).ToArray();
        ValidateDistribution(probabilities.Select(item => item.Probability));

        return new OpenAIDecisionChoiceAnswer
        {
            Choice = choices[selectedIndex],
            Confidence = ReadProbability(answer, "confidence"),
            Probabilities = probabilities
        };
    }

    private static OpenAIDecisionScoreAnswer MapScoreAnswer(
        JsonElement answer,
        IReadOnlyList<OpenAIDecisionScoreLevel> levels)
    {
        var nativeProbabilities = ReadProbabilityObject(answer, "probabilities", levels.Count);
        var probabilities = levels.Select((level, index) =>
        {
            var key = index.ToString(CultureInfo.InvariantCulture);
            if (!nativeProbabilities.TryGetValue(key, out var probability))
                throw new JsonException($"Respan score probabilities are missing level '{key}'.");
            return new OpenAIDecisionScoreProbability
            {
                Label = level.Label,
                Value = index,
                Probability = probability
            };
        }).ToArray();
        ValidateDistribution(probabilities.Select(item => item.Probability));

        return new OpenAIDecisionScoreAnswer
        {
            Score = ReadNumber(answer, "score"),
            Confidence = ReadProbability(answer, "confidence"),
            Probabilities = probabilities
        };
    }

    private static JsonObject? ParseReasoning(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Respan reasoning options must be an object.");

        var result = new JsonObject();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals("mode", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not ("auto" or "on" or "off"))
                    throw new ArgumentException("Respan reasoning.mode must be 'auto', 'on', or 'off'.");
                result["mode"] = property.Value.GetString();
            }
            else if (property.Name.Equals("budget_ms", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out var budget)
                    || budget < 0)
                    throw new ArgumentException("Respan reasoning.budget_ms must be a non-negative integer.");
                result["budget_ms"] = budget;
            }
            else
            {
                throw new NotSupportedException($"Respan Decisions does not support reasoning option '{property.Name}'.");
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static (JsonObject? Reasoning, IReadOnlyList<string> Images, JsonObject? RespanParams) ParseDecisionOptions(
        Dictionary<string, JsonElement>? properties)
    {
        JsonObject? reasoning = null;
        JsonObject? respanParams = null;
        IReadOnlyList<string> images = [];

        foreach (var (name, value) in properties ?? [])
        {
            if (name.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                reasoning = ParseReasoning(value);
            }
            else if (name.Equals("images", StringComparison.OrdinalIgnoreCase))
            {
                images = ParseImages(value);
            }
            else if (name.Equals("respan_params", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Respan respan_params must be an object.");
                respanParams = JsonNode.Parse(value.GetRawText())!.AsObject();
            }
            else
            {
                throw new NotSupportedException($"Respan Decisions does not support request option '{name}'.");
            }
        }

        return (reasoning, images, respanParams);
    }

    private static IReadOnlyList<string> ParseImages(JsonElement images)
    {
        if (images.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Respan Decisions images must be an array of base64 values or data URIs.");

        var result = new List<string>();
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(image.GetString()))
                throw new ArgumentException("Respan Decisions images must contain non-empty strings.");
            ValidateImage(image.GetString()!);
            result.Add(image.GetString()!);
        }
        return result;
    }

    private static JsonNode? MapState(
        OpenAIDecisionInput input,
        JsonElement? rawState,
        List<string> images)
    {
        if (input.Text is not null)
            return rawState.HasValue ? JsonNode.Parse(rawState.Value.GetRawText()) : JsonValue.Create(input.Text);

        var hasImages = false;
        var messages = new JsonArray();
        foreach (var message in input.Messages ?? [])
        {
            JsonNode content;
            if (message.Content.Text is not null)
            {
                content = JsonValue.Create(message.Content.Text)!;
            }
            else
            {
                var parts = new JsonArray();
                foreach (var part in message.Content.Parts ?? [])
                {
                    if (part is OpenAIDecisionInputImage image)
                    {
                        if (image.Detail is not null)
                            throw new NotSupportedException("Respan Decisions does not support per-image detail settings.");
                        ValidateImage(image.ImageUrl);
                        images.Add(image.ImageUrl);
                        hasImages = true;
                    }
                    else if (part is OpenAIDecisionInputText text)
                    {
                        parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.Text });
                    }
                    else
                    {
                        throw new NotSupportedException("Respan Decisions supports only text and image input parts.");
                    }
                }
                content = parts;
            }

            var nativeMessage = new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = content
            };
            if (message.Type is not null)
                nativeMessage["type"] = message.Type;
            messages.Add(nativeMessage);
        }

        if (!hasImages && rawState.HasValue)
            return JsonNode.Parse(rawState.Value.GetRawText());

        return messages;
    }

    private static void ValidateImage(string image)
    {
        var payload = image;
        if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = image.IndexOf(',');
            if (comma < 0
                || !image[..comma].StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                || !image[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Respan Decisions images must be base64 data URIs or base64 strings.");
            payload = image[(comma + 1)..];
        }

        try
        {
            _ = Convert.FromBase64String(payload);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Respan Decisions images must be valid base64 data URIs or base64 strings.", nameof(image), exception);
        }
    }

    private static string ChoiceKey(int index)
        => "option-" + index.ToString(CultureInfo.InvariantCulture);

    private static int ParseChoiceKey(string key, int choiceCount)
    {
        const string prefix = "option-";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0
            || index >= choiceCount
            || key != ChoiceKey(index))
            throw new JsonException("Respan selected an unknown choice.");
        return index;
    }

    private static Dictionary<string, double> ReadProbabilityObject(
        JsonElement answer,
        string propertyName,
        int expectedCount)
    {
        if (!answer.TryGetProperty(propertyName, out var probabilities)
            || probabilities.ValueKind != JsonValueKind.Object
            || probabilities.EnumerateObject().Count() != expectedCount)
            throw new JsonException($"Respan answer '{propertyName}' must contain one probability per option.");

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in probabilities.EnumerateObject())
            if (!result.TryAdd(item.Name, ReadProbabilityValue(item.Value)))
                throw new JsonException($"Respan answer '{propertyName}' contains duplicate options.");
        return result;
    }

    private static OpenAIDecisionUsage ReadUsage(JsonElement root)
    {
        var inputTokens = 0;
        var outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.ValueKind != JsonValueKind.Object)
                throw new JsonException("Respan Decisions usage must be an object.");
            inputTokens = ReadOptionalTokenCount(usage, "input_tokens");
            outputTokens = ReadOptionalTokenCount(usage, "output_tokens");
        }

        try
        {
            return new OpenAIDecisionUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            };
        }
        catch (OverflowException exception)
        {
            throw new JsonException("Respan Decisions token counts exceed the supported range.", exception);
        }
    }

    private static int ReadOptionalTokenCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return 0;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var count)
            || count < 0)
            throw new JsonException($"Respan usage '{propertyName}' must be a non-negative integer.");
        return count;
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonException($"Respan Decisions response requires a non-empty '{propertyName}'.");
        return value.GetString()!;
    }

    private static double ReadProbability(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            throw new JsonException($"Respan Decisions response requires '{propertyName}'.");
        return ReadProbabilityValue(value);
    }

    private static double ReadProbabilityValue(JsonElement value)
    {
        var probability = ReadNumberValue(value);
        if (probability is < 0 or > 1)
            throw new JsonException("Respan Decisions probabilities must be in [0, 1].");
        return probability;
    }

    private static double ReadNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            throw new JsonException($"Respan Decisions response requires '{propertyName}'.");
        return ReadNumberValue(value);
    }

    private static double ReadNumberValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
            throw new JsonException("Respan Decisions numeric values must be finite numbers.");
        return number;
    }

    private static void ValidateDistribution(IEnumerable<double> probabilities)
    {
        if (Math.Abs(probabilities.Sum() - 1) > 0.000001)
            throw new JsonException("Respan Decisions probabilities must sum to one.");
    }
}
