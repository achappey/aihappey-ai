using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LevantoLabs;

public sealed partial class LevantoLabsProvider
{
    private const string DecisionModel = "levanto-sage-v1.3";
    private const string LatestModel = "sage-latest";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record QuestionMapping(
        OpenAIDecisionQuestion Question,
        string Type,
        IReadOnlyList<string> Keys);

    private sealed record DecisionExecution(
        OpenAIDecisionResponse Response,
        IDictionary<string, string> Headers,
        JsonElement Body);

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
        => (await ExecuteOpenAIDecisionWithMetadataAsync(request, cancellationToken)).Response;

    private async Task<DecisionExecution> ExecuteOpenAIDecisionWithMetadataAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken,
        Dictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateModel(request.Model);

        var (reasoning, grounding) = ParseOptions(request.AdditionalProperties);
        if (grounding is not null)
            return await ExecuteGroundedDecisionAsync(request, reasoning, grounding
                ?? new JsonElement { }, headers, cancellationToken);

        if (TryGetImage(request.Input, out var image, out var context))
            return await ExecuteImageDecisionAsync(request, image, context, reasoning, headers, cancellationToken);

        var mappings = MapQuestions(request.Questions);
        var payload = new JsonObject
        {
            ["model"] = request.Model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase)
                ? request.Model[(GetIdentifier().Length + 1)..]
                : request.Model,
            ["state"] = JsonNode.Parse(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions).GetRawText()),
            ["questions"] = MapSystemOneQuestions(mappings)
        };
        if (reasoning is not null)
            payload["reasoning"] = reasoning;

        using var response = await SendAsync("v1/systemone", payload, headers, cancellationToken);
        var (root, result) = await ReadAndMapResponseAsync(
            response, body => (body.Clone(), MapSystemOneResponse(body, mappings)),
            "LevantoLabs returned an invalid SystemOne response.", cancellationToken);
        return new(result, response.GetHeaders(), root);
    }

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        ValidateModel(request.Model);
        var native = request.ToOpenAIDecisionRequest(GetIdentifier());
        if (request.Reasoning.HasValue)
        {
            ReadReasoning(request.Reasoning);
            (native.AdditionalProperties ??= new(StringComparer.OrdinalIgnoreCase))["reasoning"] =
                request.Reasoning.Value.Clone();
        }

        var result = await ExecuteOpenAIDecisionResultAsync(
            native,
            cancellationToken,
            request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()));
        var response = result.ToDecisionResponse(request, GetIdentifier());
        if (result.Headers.TryGetValue("x-request-id", out var requestId))
            response.Response!.Id = requestId;
        return response;
    }

    private async Task<OpenAICompatibleDecisionResult> ExecuteOpenAIDecisionResultAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken,
        Dictionary<string, string>? headers = null)
    {
        var result = await ExecuteOpenAIDecisionWithMetadataAsync(request, cancellationToken, headers);
        return new(result.Response, result.Headers, result.Body);
    }

    private async Task<DecisionExecution> ExecuteGroundedDecisionAsync(
        OpenAIDecisionRequest request,
        string? reasoning,
        JsonElement grounding,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        if (request.Questions.Count != 1 || request.Questions[0] is not OpenAIDecisionPredicateQuestion predicate)
            throw new NotSupportedException("LevantoLabs grounding is supported for one Yes/No question at a time.");
        if (TryGetImage(request.Input, out _, out _))
            throw new NotSupportedException("LevantoLabs grounding cannot be combined with image input.");

        var content = SerializeState(request.Input);
        var id = predicate.Name ?? "question-0";
        var payload = new JsonObject
        {
            ["content"] = content,
            ["question"] = new JsonObject
            {
                ["id"] = id,
                ["kind"] = "yesno",
                ["instructions"] = predicate.Instructions
            },
            ["grounding"] = JsonNode.Parse(grounding.GetRawText())
        };
        if (reasoning is not null)
            payload["reasoning"] = reasoning;

        using var response = await SendAsync("decide", payload, headers, cancellationToken);
        var (root, result) = await ReadAndMapResponseAsync(
            response, body =>
            {
                var raw = body.Clone();
                return (raw, MapDecideResponse(raw, predicate, id));
            },
            "LevantoLabs returned an invalid Sage decision response.", cancellationToken);
        return new(result, response.GetHeaders(), root);
    }

    private async Task<DecisionExecution> ExecuteImageDecisionAsync(
        OpenAIDecisionRequest request,
        string image,
        string? context,
        string? reasoning,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        if (request.Questions.Count != 1 || request.Questions[0] is not OpenAIDecisionPredicateQuestion predicate)
            throw new NotSupportedException("LevantoLabs image input is supported for one Yes/No question at a time.");

        var imageContent = new JsonObject
        {
            ["kind"] = "image",
            ["media"] = image
        };
        if (context is not null)
            imageContent["text"] = context;
        var payload = new JsonObject
        {
            ["content"] = imageContent,
            ["question"] = new JsonObject
            {
                ["id"] = predicate.Name ?? "question-0",
                ["kind"] = "yesno",
                ["instructions"] = predicate.Instructions
            }
        };
        if (reasoning is not null)
            payload["reasoning"] = reasoning;

        using var response = await SendAsync("decide", payload, headers, cancellationToken);
        var (root, result) = await ReadAndMapResponseAsync(
            response, body =>
            {
                var raw = body.Clone();
                return (raw, MapDecideResponse(raw, predicate, predicate.Name ?? "question-0"));
            },
            "LevantoLabs returned an invalid Sage decision response.", cancellationToken);
        return new(result, response.GetHeaders(), root);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string endpoint,
        JsonObject payload,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(DecisionJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var (name, value) in headers ?? [])
            request.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        var reason = response.ReasonPhrase;
        response.Dispose();
        throw new HttpRequestException(
            $"LevantoLabs Decisions request failed ({(int)status} {reason}): {raw}",
            null,
            status);
    }

    private static IReadOnlyList<QuestionMapping> MapQuestions(
        IReadOnlyList<OpenAIDecisionQuestion> questions)
    {
        var result = new List<QuestionMapping>(questions.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            if (question.Name is not null && string.IsNullOrWhiteSpace(question.Name))
                throw new ArgumentException("LevantoLabs question IDs must not be empty.", nameof(questions));
            var id = question.Name ?? "question-" + index.ToString(CultureInfo.InvariantCulture);
            if (!ids.Add(id))
                throw new ArgumentException("LevantoLabs SystemOne requires unique question IDs.", nameof(questions));

            switch (question)
            {
                case OpenAIDecisionPredicateQuestion:
                    result.Add(new(question, "noul", []));
                    break;
                case OpenAIDecisionChoiceQuestion choice:
                    if (choice.Choices.Count is < 2 or > 120)
                        throw new ArgumentException("LevantoLabs SystemOne choice questions require 2 to 120 options.", nameof(questions));
                    result.Add(new(question, "choice",
                        Enumerable.Range(0, choice.Choices.Count)
                            .Select(option => "option-" + option.ToString(CultureInfo.InvariantCulture))
                            .ToArray()));
                    break;
                case OpenAIDecisionScoreQuestion score:
                    if (score.Levels.Count is < 2 or > 26)
                        throw new ArgumentException("LevantoLabs SystemOne scale questions require 2 to 26 levels.", nameof(questions));
                    result.Add(new(question, "score", []));
                    break;
                default:
                    throw new NotSupportedException($"LevantoLabs does not support decision question '{question.Type}'.");
            }
        }

        return result;
    }

    private static JsonObject MapSystemOneQuestions(IReadOnlyList<QuestionMapping> mappings)
    {
        var questions = new JsonObject();
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            var question = mapping.Question;
            var id = question.Name ?? "question-" + index.ToString(CultureInfo.InvariantCulture);
            var native = new JsonObject
            {
                ["type"] = mapping.Type,
                ["instructions"] = question.Instructions
            };

            switch (question)
            {
                case OpenAIDecisionChoiceQuestion choice:
                    {
                        var criteria = new JsonObject();
                        for (var option = 0; option < mapping.Keys.Count; option++)
                            criteria[mapping.Keys[option]] = choice.Choices[option].Description;
                        native["criteria"] = criteria;
                        break;
                    }
                case OpenAIDecisionScoreQuestion score:
                    {
                        var criteria = new JsonArray();
                        foreach (var level in score.Levels)
                            criteria.Add(level.Description ?? level.Label);
                        native["criteria"] = criteria;
                        break;
                    }
            }

            questions[id] = native;
        }
        return questions;
    }

    private static OpenAIDecisionResponse MapSystemOneResponse(
        JsonElement body,
        IReadOnlyList<QuestionMapping> mappings)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !body.TryGetProperty("answers", out var nativeAnswers)
            || nativeAnswers.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("LevantoLabs returned an invalid SystemOne response.");

        var inputTokens = ReadNonNegativeInt(usage, "input_tokens");
        var outputTokens = ReadNonNegativeInt(usage, "output_tokens");
        var answers = new List<OpenAIDecisionAnswer>(mappings.Count);
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            var id = mapping.Question.Name ?? "question-" + index.ToString(CultureInfo.InvariantCulture);
            if (!nativeAnswers.TryGetProperty(id, out var answer)
                || !answer.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != mapping.Type)
                throw new InvalidOperationException($"LevantoLabs omitted or mismatched answer '{id}'.");

            switch (mapping.Question)
            {
                case OpenAIDecisionPredicateQuestion:
                    {
                        var probability = ReadProbability(answer.GetProperty("noul"));
                        answers.Add(new OpenAIDecisionPredicateAnswer
                        {
                            Name = mapping.Question.Name,
                            Probability = probability
                        });
                        break;
                    }
                case OpenAIDecisionChoiceQuestion choice:
                    {
                        var selected = answer.GetProperty("choice").GetString()
                            ?? throw new InvalidOperationException($"LevantoLabs returned an empty choice for '{id}'.");
                        var selectedIndex = Array.IndexOf(mapping.Keys.ToArray(), selected);
                        if (selectedIndex < 0)
                            throw new InvalidOperationException($"LevantoLabs returned an unknown choice for '{id}'.");
                        var probabilities = ReadDistribution(answer.GetProperty("probabilities"), mapping.Keys);
                        if (probabilities[selected] + 1e-6 < probabilities.Values.Max())
                            throw new InvalidOperationException($"LevantoLabs selected a non-maximal choice for '{id}'.");
                        answers.Add(new OpenAIDecisionChoiceAnswer
                        {
                            Name = mapping.Question.Name,
                            Choice = choice.Choices[selectedIndex].Value,
                            Confidence = ReadConfidence(answer, probabilities.Values.Max()),
                            Probabilities = mapping.Keys.Select((key, option) => new OpenAIDecisionChoiceProbability
                            {
                                Value = choice.Choices[option].Value,
                                Probability = probabilities[key]
                            }).ToArray()
                        });
                        break;
                    }
                case OpenAIDecisionScoreQuestion score:
                    {
                        var value = answer.GetProperty("score").GetDouble();
                        if (!double.IsFinite(value) || value < 0 || value > score.Levels.Count - 1)
                            throw new InvalidOperationException($"LevantoLabs returned an invalid scale score for '{id}'.");
                        var keys = Enumerable.Range(0, score.Levels.Count)
                            .Select(level => level.ToString(CultureInfo.InvariantCulture)).ToArray();
                        var probabilities = ReadDistribution(answer.GetProperty("probabilities"), keys);
                        var weightedValue = probabilities.Sum(item =>
                            int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                        if (Math.Abs(value - weightedValue) > 1e-6)
                            throw new InvalidOperationException($"LevantoLabs returned an inconsistent scale distribution for '{id}'.");
                        answers.Add(new OpenAIDecisionScoreAnswer
                        {
                            Name = mapping.Question.Name,
                            Score = value,
                            Confidence = ReadConfidence(answer, probabilities.Values.Max()),
                            Probabilities = keys.Select((key, level) => new OpenAIDecisionScoreProbability
                            {
                                Label = score.Levels[level].Label,
                                Value = level,
                                Probability = probabilities[key]
                            }).ToArray()
                        });
                        break;
                    }
            }
        }

        if (nativeAnswers.EnumerateObject().Count() != mappings.Count)
            throw new InvalidOperationException("LevantoLabs returned unexpected SystemOne answers.");

        return new()
        {
            Model = model.GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            }
        };
    }

    private static OpenAIDecisionResponse MapDecideResponse(
        JsonElement body,
        OpenAIDecisionPredicateQuestion question,
        string id)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("id", out var responseId)
            || responseId.ValueKind != JsonValueKind.String
            || responseId.GetString() != id
            || !body.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("answer", out var answer)
            || answer.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || (answer.ValueKind == JsonValueKind.String && answer.GetString() is not ("yes" or "no"))
            || !body.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object
            || !meta.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString()))
            throw new InvalidOperationException("LevantoLabs returned an invalid Sage decision response.");

        var probability = ReadProbability(result.GetProperty("probability"));
        var tokenUsage = meta.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? usage
            : default;
        var inputTokens = tokenUsage.ValueKind == JsonValueKind.Object
            ? ReadNonNegativeInt(tokenUsage, "input_tokens")
            : 0;
        var outputTokens = tokenUsage.ValueKind == JsonValueKind.Object
            ? ReadNonNegativeInt(tokenUsage, "output_tokens")
            : 0;
        return new()
        {
            Model = model.GetString()!,
            Answers =
            [
                new OpenAIDecisionPredicateAnswer
                {
                    Name = question.Name,
                    Probability = probability
                }
            ],
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
        };
    }

    private static (string? Reasoning, JsonElement? Grounding) ParseOptions(
        Dictionary<string, JsonElement>? properties)
    {
        string? reasoning = null;
        JsonElement? grounding = null;
        foreach (var (name, value) in properties ?? [])
        {
            if (name.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
            {
                if (value.ValueKind != JsonValueKind.String
                    || value.GetString() is not ("off" or "auto" or "on"))
                    throw new ArgumentException("LevantoLabs reasoning must be 'off', 'auto', or 'on'.");
                reasoning = value.GetString();
            }
            else if (name.Equals("grounding", StringComparison.OrdinalIgnoreCase))
            {
                ValidateGrounding(value);
                grounding = value.Clone();
            }
            else
                throw new NotSupportedException($"LevantoLabs does not support decision option '{name}'.");
        }
        return (reasoning, grounding);
    }

    private static void ValidateGrounding(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("LevantoLabs grounding must be an object.");
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("trigger"))
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() is not ("never" or "low_confidence" or "always"))
                    throw new ArgumentException("LevantoLabs grounding.trigger must be never, low_confidence, or always.");
            }
            else if (property.NameEquals("confidence_floor"))
            {
                var confidence = ReadProbability(property.Value);
                if (confidence < 0 || confidence > 1)
                    throw new ArgumentException("LevantoLabs grounding.confidence_floor must be in [0, 1].");
            }
            else
                throw new NotSupportedException($"LevantoLabs does not support grounding option '{property.Name}'.");
        }
    }

    private static string? ReadReasoning(JsonElement? reasoning)
    {
        if (!reasoning.HasValue)
            return null;
        var value = reasoning.Value;
        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not ("off" or "auto" or "on"))
            throw new ArgumentException("LevantoLabs reasoning must be 'off', 'auto', or 'on'.");
        return value.GetString();
    }

    private static async Task<T> ReadAndMapResponseAsync<T>(
        HttpResponseMessage response,
        Func<JsonElement, T> map,
        string error,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(raw);
            return map(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or FormatException or OverflowException or InvalidOperationException)
        {
            throw new InvalidOperationException(error, exception);
        }
    }

    private static JsonNode SerializeState(OpenAIDecisionInput input)
        => JsonNode.Parse(JsonSerializer.SerializeToElement(input, DecisionJsonOptions).GetRawText())
            ?? throw new ArgumentException("LevantoLabs decision content cannot be null.", nameof(input));

    private static bool TryGetImage(OpenAIDecisionInput input, out string image, out string? context)
    {
        image = "";
        context = null;
        var images = new List<OpenAIDecisionInputImage>();
        var text = new List<string>();
        if (input.Messages is null)
            return false;
        foreach (var message in input.Messages)
        {
            if (message.Content.Text is not null)
                text.Add(message.Content.Text);
            foreach (var part in message.Content.Parts ?? [])
            {
                switch (part)
                {
                    case OpenAIDecisionInputText inputText:
                        text.Add(inputText.Text);
                        break;
                    case OpenAIDecisionInputImage inputImage:
                        images.Add(inputImage);
                        break;
                    default:
                        throw new NotSupportedException("LevantoLabs supports only text and image decision input.");
                }
            }
        }

        if (images.Count == 0)
            return false;
        if (images.Count != 1)
            throw new NotSupportedException("LevantoLabs Sage accepts one image per decision.");
        var imageUrl = images[0].ImageUrl;
        if (!imageUrl.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase)
            && !imageUrl.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase)
            && !imageUrl.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("LevantoLabs Sage images must be PNG, JPEG, or WebP data URLs.");
        image = imageUrl;
        context = text.Count == 0 ? null : string.Join(Environment.NewLine, text);
        return true;
    }

    private static Dictionary<string, double> ReadDistribution(
        JsonElement value,
        IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("LevantoLabs probability distribution must be an object.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, ReadProbability(item.Value)))
                throw new InvalidOperationException("LevantoLabs returned an unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new InvalidOperationException("LevantoLabs returned an incomplete or unnormalized probability distribution.");
        return result;
    }

    private static double ReadConfidence(JsonElement answer, double fallback)
    {
        if (!answer.TryGetProperty("confidence", out var value))
            throw new InvalidOperationException("LevantoLabs did not report decision confidence.");
        var confidence = ReadProbability(value);
        if (Math.Abs(confidence - fallback) > 1e-6)
            throw new InvalidOperationException("LevantoLabs confidence must equal the highest probability.");
        return confidence;
    }

    private static int ReadNonNegativeInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || !value.TryGetInt32(out var number)
            || number < 0)
            throw new InvalidOperationException($"LevantoLabs returned invalid '{property}' usage.");
        return number;
    }

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new InvalidOperationException("LevantoLabs probabilities must be finite values in [0, 1].");
        return number;
    }

    private static void ValidateModel(string model)
    {
        var native = model.StartsWith("levantolabs/", StringComparison.OrdinalIgnoreCase)
            ? model["levantolabs/".Length..]
            : model;
        if (native is not (DecisionModel or LatestModel))
            throw new NotSupportedException($"LevantoLabs does not document model '{model}'.");
    }
}
