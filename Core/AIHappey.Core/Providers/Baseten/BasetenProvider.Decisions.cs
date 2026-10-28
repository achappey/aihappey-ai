using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Baseten;

public sealed partial class BasetenProvider
{
    private const int BasetenDecisionContextWindow = 16_384;
    private const string BasetenDecisionEndpoint = "v1/systemone";
    private const string BasetenElephantModel = "decision-models/baseten-elephant-9b-latest";
    private const string BasetenMammothModel = "decision-models/baseten-mammoth-27b-latest";

    private static readonly IReadOnlyDictionary<string, (string Name, string Description)> BasetenDecisionModels =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [BasetenElephantModel] = ("Elephant-1", "Baseten serverless decision model (9B)."),
            [BasetenMammothModel] = ("Mammoth-1", "Baseten serverless decision model (27B).")
        };

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sdkState = request.StateFormat == "sdk";
        request = PrepareBasetenDecisionRequest(request);
        var model = GetBasetenDecisionModel(request.Model);
        var state = sdkState
            ? MapBasetenSdkState(request.State.Value)
            : JsonNode.Parse(request.State.Value.GetRawText())
                ?? throw new ArgumentException("Baseten decision state cannot be null.", nameof(request));
        var (tier, questions) = (GetBasetenServiceTier(request.ProviderOptions), MapBasetenQuestions(request.Questions));
        var (body, headers) = await SendBasetenDecisionAsync(
            model, state, questions, tier, request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);

        try
        {
            var result = MapBasetenDecisionResponse(body, request, model);
            var requestId = ReadBasetenString(body, "request_id");
            if (requestId is null && headers.TryGetValue("x-request-id", out var headerRequestId))
                requestId = headerRequestId;
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = requestId,
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Baseten returned an invalid decision response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("Baseten Decisions does not support safety_identifier.");
        if (request.AdditionalProperties?.Count > 0
            && request.AdditionalProperties.Keys.Any(key => !key.Equals("service_tier", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Baseten Decisions supports only the service_tier additional request option.");
        var tier = GetBasetenServiceTierFromOpenAI(request.AdditionalProperties);
        var model = GetBasetenDecisionModel(request.Model);
        var typedRequest = CreateBasetenTypedRequest(request);
        var nativeQuestions = MapBasetenQuestions(typedRequest.Questions);
        var (body, _) = await SendBasetenDecisionAsync(
            model, MapBasetenOpenAIInput(request.Input), nativeQuestions, tier, null, cancellationToken);

        try
        {
            var mapped = MapBasetenDecisionResponse(body, typedRequest, model);
            var answers = new List<OpenAIDecisionAnswer>(request.Questions.Count);
            for (var index = 0; index < request.Questions.Count; index++)
            {
                var question = request.Questions[index];
                var id = BasetenOpenAIQuestionId(index);
                var native = body.GetProperty("answers").GetProperty(id);
                var answer = mapped.Answers[id];
                OpenAIDecisionAnswer openAIAnswer = (question, answer) switch
                {
                    (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                        new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                    (OpenAIDecisionChoiceQuestion choice, DecisionBooleanAnswer boolean)
                        when IsBasetenBooleanChoice(choice) =>
                        MapBasetenOpenAIBooleanChoice(choice, boolean),
                    (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                        MapBasetenOpenAIChoice(choice, selected, native),
                    (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                        MapBasetenOpenAIScore(score, scored, native),
                    _ => throw new InvalidOperationException("Unexpected Baseten decision answer type.")
                };
                openAIAnswer.Name = question.Name;
                answers.Add(openAIAnswer);
            }

            var inputTokens = mapped.Usage!.InputTokens!.Value;
            var outputTokens = mapped.Usage.OutputTokens!.Value;
            if ((long)inputTokens + outputTokens > int.MaxValue)
                throw new JsonException("Invalid decision usage.");
            return new()
            {
                Model = model,
                Answers = answers,
                Usage = new()
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = inputTokens + outputTokens
                },
                AdditionalProperties = new()
                {
                    ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                    {
                        [GetIdentifier()] = body.Clone()
                    }),
                    ["warnings"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new { type = "other", message = "Baseten System One reports one output token per question; output_tokens reflects that documented count." }
                    })
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Baseten returned an invalid decision response.", exception);
        }
    }

    private DecisionRequest PrepareBasetenDecisionRequest(DecisionRequest request)
    {
        var validationRequest = request.StateFormat == "sdk"
            ? new DecisionRequest
            {
                Model = request.Model,
                State = request.State,
                Questions = request.Questions,
                Headers = request.Headers,
                ProviderOptions = request.ProviderOptions
            }
            : request;
        return validationRequest.PrepareForProvider(GetIdentifier());
    }

    private static string GetBasetenDecisionModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = "baseten/";
        var nativeModel = model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
        if (!BasetenDecisionModels.ContainsKey(nativeModel))
            throw new NotSupportedException($"Baseten Decisions supports only {BasetenElephantModel} and {BasetenMammothModel}.");
        return nativeModel;
    }

    private static JsonObject MapBasetenQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var mapped = new JsonObject();
        foreach (var (id, question) in questions)
        {
            var instructions = question.Instructions
                ?? throw new ArgumentException($"Baseten question '{id}' requires instructions.");
            var native = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = RenderBasetenDecisionText(instructions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    var booleanCriteria = new JsonObject();
                    if (boolean.Criteria.True is not null)
                        booleanCriteria["true"] = RenderBasetenDecisionText(boolean.Criteria.True);
                    if (boolean.Criteria.False is not null)
                        booleanCriteria["false"] = RenderBasetenDecisionText(boolean.Criteria.False);
                    if (booleanCriteria.Count > 0)
                        native["criteria"] = booleanCriteria;
                    break;
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Any(item => string.IsNullOrWhiteSpace(item.Key)))
                        throw new ArgumentException($"Baseten choice question '{id}' requires nonempty option names.");
                    var choices = new JsonObject();
                    foreach (var (key, description) in choice.Criteria)
                        choices[key] = description is null ? key : RenderBasetenDecisionText(description);
                    native["criteria"] = choices;
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Any(level => level is null))
                        throw new NotSupportedException($"Baseten score question '{id}' requires text for every level.");
                    var levels = new JsonArray();
                    foreach (var level in score.Criteria)
                        levels.Add(RenderBasetenDecisionText(level!));
                    native["criteria"] = levels;
                    break;
                default:
                    throw new NotSupportedException($"Baseten System One does not support question type '{question.Type}'.");
            }
            mapped[id] = native;
        }
        return mapped;
    }

    private static JsonNode RenderBasetenDecisionText(DecisionInput input)
        => JsonNode.Parse(input.Value.GetRawText()) is JsonValue value && value.TryGetValue<string>(out var text)
            ? JsonValue.Create(text)!
            : JsonValue.Create(input.Value.GetRawText())!;

    private static JsonNode MapBasetenSdkState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array || state.GetArrayLength() == 0)
            throw new ArgumentException("SDK decision state must be a nonempty array of parts.");
        var content = new JsonArray();
        foreach (var part in state.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("SDK decision state requires typed parts.");
            ValidateBasetenSdkPartOptions(part);
            var type = RequiredBasetenString(part, "type");
            switch (type)
            {
                case "text":
                    RequireBasetenOnly(part, "type", "text", "providerOptions");
                    AddBasetenText(content, RequiredBasetenString(part, "text"));
                    break;
                case "json":
                    RequireBasetenOnly(part, "type", "value");
                    if (!part.TryGetProperty("value", out var value))
                        throw new ArgumentException("JSON parts require a value.");
                    AddBasetenText(content, value.GetRawText());
                    break;
                case "file":
                    RequireBasetenOnly(part, "type", "data", "mediaType", "filename", "providerOptions");
                    MapBasetenSdkFile(content, part);
                    break;
                default:
                    throw new NotSupportedException($"Baseten Decisions does not support SDK state part '{type}'.");
            }
        }
        return new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content
            })
        };
    }

    private static void ValidateBasetenSdkPartOptions(JsonElement part)
    {
        if (!part.TryGetProperty("providerOptions", out var options))
            return;
        if (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())
            throw new NotSupportedException("Baseten Decisions does not support state-part providerOptions.");
    }

    private static void MapBasetenSdkFile(JsonArray content, JsonElement part)
    {
        var mediaType = RequiredBasetenString(part, "mediaType");
        if (!part.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SDK file parts require tagged data.");
        var kind = RequiredBasetenString(data, "type");
        if (part.TryGetProperty("filename", out var filename))
            AddBasetenText(content, "File: " + (filename.ValueKind == JsonValueKind.String
                ? filename.GetString()
                : throw new ArgumentException("SDK filenames must be strings.")));
        if (kind == "text")
        {
            RequireBasetenOnly(data, "type", "text");
            if (!(mediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException($"Baseten Decisions does not support inline text files of media type '{mediaType}'.");
            AddBasetenText(content, RequiredBasetenString(data, "text"));
            return;
        }
        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Baseten Decisions does not support SDK files of media type '{mediaType}'.");

        string url;
        switch (kind)
        {
            case "data":
                RequireBasetenOnly(data, "type", "data");
                var base64 = RequiredBasetenString(data, "data");
                try { _ = Convert.FromBase64String(base64); }
                catch (FormatException exception) { throw new ArgumentException("SDK image data must be base64.", exception); }
                url = $"data:{mediaType};base64,{base64}";
                break;
            case "url":
                RequireBasetenOnly(data, "type", "url", "originalUrl");
                url = data.TryGetProperty("originalUrl", out _)
                    ? RequiredBasetenString(data, "originalUrl")
                    : RequiredBasetenString(data, "url");
                if (url.StartsWith($"data:{mediaType};base64,", StringComparison.OrdinalIgnoreCase))
                {
                    try { _ = Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]); }
                    catch (FormatException exception) { throw new ArgumentException("SDK image data must be base64.", exception); }
                }
                else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new NotSupportedException("Baseten Decisions supports image data URLs and publicly accessible HTTPS image URLs.");
                break;
            default:
                throw new NotSupportedException($"Baseten Decisions does not support SDK file data '{kind}'.");
        }
        content.Add(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = url }
        });
    }

    private static void AddBasetenText(JsonArray content, string text)
        => content.Add(new JsonObject { ["type"] = "text", ["text"] = text });

    private static string RequiredBasetenString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"SDK decision part requires string '{name}'.");

    private static void RequireBasetenOnly(JsonElement value, params string[] names)
    {
        if (value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            throw new NotSupportedException("Unrecognized SDK decision part fields cannot be forwarded without loss.");
    }

    private static string? GetBasetenServiceTier(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue("baseten", out var value) != true)
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.baseten must be an object.");
        string? tier = null;
        foreach (var option in value.EnumerateObject())
        {
            if (!option.Name.Equals("service_tier", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Baseten Decisions does not support provider option '{option.Name}'.");
            tier = ParseBasetenServiceTier(option.Value);
        }
        return tier;
    }

    private static string? GetBasetenServiceTierFromOpenAI(Dictionary<string, JsonElement>? options)
    {
        if (options is null || options.Count == 0)
            return null;
        string? tier = null;
        foreach (var option in options)
        {
            if (!option.Key.Equals("service_tier", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Baseten Decisions does not support request option '{option.Key}'.");
            tier = ParseBasetenServiceTier(option.Value);
        }
        return tier;
    }

    private static string? ParseBasetenServiceTier(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("default" or "priority"))
            throw new ArgumentException("Baseten service_tier must be 'default' or 'priority'.");
        return value.GetString();
    }

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SendBasetenDecisionAsync(
        string model, JsonNode state, JsonObject questions, string? serviceTier,
        Dictionary<string, string>? passthroughHeaders, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = state,
            ["questions"] = questions
        };
        if (serviceTier is not null)
            payload["service_tier"] = serviceTier;
        using var request = new HttpRequestMessage(HttpMethod.Post, BasetenDecisionEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        foreach (var (name, value) in passthroughHeaders ?? [])
            request.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Baseten decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);
        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.Clone(), headers);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Baseten returned an invalid decision response.", exception);
        }
    }

    private static DecisionResponse MapBasetenDecisionResponse(
        JsonElement body, DecisionRequest request, string model)
    {
        if (ReadBasetenString(body, "model") != model)
            throw new JsonException("Decision model mismatch.");
        var native = body.GetProperty("answers");
        if (native.ValueKind != JsonValueKind.Object
            || native.EnumerateObject().Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = native.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (ReadBasetenString(answer, "type") != expectedType)
                throw new JsonException("Answer type does not match question.");
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadBasetenProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadBasetenProbability(answer.GetProperty("confidence"));
                    var distribution = ReadBasetenDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = ReadBasetenString(answer, "choice")
                        ?? throw new JsonException("Missing selected choice.");
                    if (!distribution.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice must be a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    ReadBasetenProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(BasetenDecisionIndex).ToArray();
                    var probabilities = ReadBasetenDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label)
                            || label.ValueKind != JsonValueKind.String
                            || label.GetString() != RenderBasetenDecisionText(score.Criteria[int.Parse(key, CultureInfo.InvariantCulture)]!).GetValue<string>()))
                        throw new JsonException("Invalid score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = probabilities.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    var roundingTolerance = Math.Max(0.0001, (keys.Length * (keys.Length - 1) / 2 + 1) * 0.00005);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1
                        || Math.Abs(value - expected) > roundingTolerance)
                        throw new JsonException("Score must be the probability-weighted level position.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                default:
                    throw new JsonException("Unsupported decision question type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        if (inputTokens < 0 || outputTokens < 0)
            throw new JsonException("Invalid decision usage.");
        return new()
        {
            Answers = answers,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }

    private static Dictionary<string, double> ReadBasetenDistribution(
        JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name)
                || !result.TryAdd(property.Name, ReadBasetenProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        var roundingTolerance = Math.Max(0.001, expected.Count * 0.00005);
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > roundingTolerance)
            throw new JsonException("Incomplete or unnormalized probability distribution.");
        return result;
    }

    private static double ReadBasetenProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Expected a finite probability in [0,1].");
        return number;
    }

    private static string? ReadBasetenString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string BasetenDecisionIndex(int index)
        => index.ToString(CultureInfo.InvariantCulture);

    private static DecisionRequest CreateBasetenTypedRequest(OpenAIDecisionRequest request)
    {
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            var id = BasetenOpenAIQuestionId(index);
            questions[id] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion
                {
                    Instructions = question.Instructions
                },
                OpenAIDecisionChoiceQuestion choice when IsBasetenBooleanChoice(choice) =>
                    new DecisionBooleanQuestion
                    {
                        Instructions = question.Instructions,
                        Criteria = new DecisionBooleanCriteria
                        {
                            True = FindBasetenBooleanDescription(choice, true),
                            False = FindBasetenBooleanDescription(choice, false)
                        }
                    },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, optionIndex) =>
                        KeyValuePair.Create($"option-{optionIndex}", (DecisionInput?)new DecisionInput(
                            DescribeBasetenChoice(option.Value, option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeBasetenLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }
        return new()
        {
            Model = request.Model,
            State = new DecisionInput(JsonSerializer.SerializeToElement(new { })),
            Questions = questions
        };
    }

    private static JsonNode MapBasetenOpenAIInput(OpenAIDecisionInput input)
    {
        if (input.Text is not null)
            return JsonValue.Create(input.Text)!;
        var messages = new JsonArray();
        foreach (var message in input.Messages ?? [])
        {
            var mappedMessage = new JsonObject { ["role"] = "user" };
            if (message.Content.Text is not null)
                mappedMessage["content"] = message.Content.Text;
            else
            {
                var parts = new JsonArray();
                foreach (var part in message.Content.Parts ?? [])
                {
                    switch (part)
                    {
                        case OpenAIDecisionInputText text:
                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                            break;
                        case OpenAIDecisionInputImage image when image.Detail is null:
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = image.ImageUrl }
                            });
                            break;
                        case OpenAIDecisionInputImage:
                            throw new NotSupportedException("Baseten Decisions does not document OpenAI image detail settings.");
                        default:
                            throw new NotSupportedException($"Baseten Decisions does not support input part '{part.Type}'.");
                    }
                }
                mappedMessage["content"] = parts;
            }
            messages.Add(mappedMessage);
        }
        return new JsonObject { ["messages"] = messages };
    }

    private static string BasetenOpenAIQuestionId(int index)
        => "question-" + BasetenDecisionIndex(index);

    private static bool IsBasetenBooleanChoice(OpenAIDecisionChoiceQuestion choice)
        => choice.Choices.Count == 2
            && choice.Choices.Count(option => option.Value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) == 2
            && choice.Choices.Select(option => option.Value.Value.GetBoolean()).Distinct().Count() == 2;

    private static DecisionInput? FindBasetenBooleanDescription(OpenAIDecisionChoiceQuestion choice, bool value)
    {
        var option = choice.Choices.Single(item => item.Value.Value.GetBoolean() == value);
        return option.Description is null ? null : new DecisionInput(option.Description);
    }

    private static string DescribeBasetenChoice(OpenAIDecisionChoiceValue value, string? description)
    {
        var label = value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()!
            : value.Value.GetRawText();
        return DescribeBasetenLevel(label, description);
    }

    private static string DescribeBasetenLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;

    private static OpenAIDecisionAnswer MapBasetenOpenAIBooleanChoice(
        OpenAIDecisionChoiceQuestion question, DecisionBooleanAnswer answer)
    {
        var trueChoice = question.Choices.Single(item => item.Value.Value.GetBoolean());
        var falseChoice = question.Choices.Single(item => !item.Value.Value.GetBoolean());
        return new OpenAIDecisionChoiceAnswer
        {
            Choice = answer.Probability >= 0.5 ? trueChoice.Value : falseChoice.Value,
            Confidence = Math.Max(answer.Probability, 1 - answer.Probability),
            Probabilities =
            [
                new() { Value = trueChoice.Value, Probability = answer.Probability },
                new() { Value = falseChoice.Value, Probability = 1 - answer.Probability }
            ]
        };
    }

    private static OpenAIDecisionAnswer MapBasetenOpenAIChoice(
        OpenAIDecisionChoiceQuestion question, DecisionChoiceAnswer answer, JsonElement native)
    {
        var index = ParseBasetenOptionIndex(answer.Choice);
        if (index >= question.Choices.Count)
            throw new InvalidOperationException("Baseten returned an unknown choice identifier.");
        return new OpenAIDecisionChoiceAnswer
        {
            Choice = question.Choices[index].Value,
            Confidence = ReadBasetenProbability(native.GetProperty("confidence")),
            Probabilities = question.Choices.Select((option, optionIndex) =>
                new OpenAIDecisionChoiceProbability
                {
                    Value = option.Value,
                    Probability = answer.Probabilities![$"option-{optionIndex}"]
                }).ToArray()
        };
    }

    private static OpenAIDecisionAnswer MapBasetenOpenAIScore(
        OpenAIDecisionScoreQuestion question, DecisionScoreAnswer answer, JsonElement native)
        => new OpenAIDecisionScoreAnswer
        {
            Score = answer.Score,
            Confidence = ReadBasetenProbability(native.GetProperty("confidence")),
            Probabilities = question.Levels.Select((level, index) =>
            {
                var key = BasetenDecisionIndex(index);
                return new OpenAIDecisionScoreProbability
                {
                    Label = level.Label,
                    Value = index,
                    Probability = answer.Probabilities![key]
                };
            }).ToArray()
        };

    private static int ParseBasetenOptionIndex(string option)
    {
        const string prefix = "option-";
        if (!option.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(option[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0)
            throw new InvalidOperationException("Baseten returned an invalid option identifier.");
        return index;
    }
}
