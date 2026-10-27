using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DigitalOcean;

public partial class DigitalOceanProvider
{
    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var sdkState = request.StateFormat == "sdk";
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var prefix = GetIdentifier() + "/";
        var model = request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? request.Model[prefix.Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.digitalocean must be an object.", nameof(request));
            if (options.EnumerateObject().Any())
                throw new NotSupportedException("DigitalOcean System One does not support provider options, including streaming or canonical field overrides.");
        }

        JsonNode? state = JsonNode.Parse(request.State.Value.GetRawText());
        if (sdkState)
        {
            var input = request.ToOpenAIDecisionRequest(GetIdentifier()).Input;
            var texts = new JsonArray();
            foreach (var message in input.Messages ?? [])
                foreach (var part in message.Content.Parts ?? [])
                    if (part is OpenAIDecisionInputText text)
                        texts.Add(text.Text);
                    else
                        throw new NotSupportedException("DigitalOcean System One supports only text and JSON evidence, not image, audio, or video inputs.");
            state = texts;
        }
        else if (request.State.Value.ValueKind == JsonValueKind.Array
            && request.State.Value.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            throw new NotSupportedException("DigitalOcean System One state arrays must contain only text values; use an object for structured JSON evidence.");

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (question.Instructions!.Value.ValueKind != JsonValueKind.String)
                throw new NotSupportedException($"DigitalOcean question '{id}' requires text instructions.");
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = question.Instructions.Value.GetString()
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null }:
                    throw new NotSupportedException("DigitalOcean noul questions do not support boolean criteria; put the statement in instructions.");
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Any(item => string.IsNullOrWhiteSpace(item.Key)
                        || item.Value?.Value.ValueKind != JsonValueKind.String))
                        throw new ArgumentException($"DigitalOcean choice question '{id}' requires nonempty labels with text descriptions.", nameof(request));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria);
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Any(level => level is null
                        || (level.Value.ValueKind != JsonValueKind.String
                            && !(level.Value.ValueKind == JsonValueKind.Object
                                && level.Value.TryGetProperty("label", out var label)
                                && label.ValueKind == JsonValueKind.String))))
                        throw new ArgumentException($"DigitalOcean score question '{id}' requires text levels or objects with a text label.", nameof(request));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria);
                    break;
                default:
                    throw new NotSupportedException($"DigitalOcean does not support question type '{question.Type}'.");
            }
            questions[id] = mapped;
        }

        using var messageRequest = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model, ["state"] = state, ["questions"] = questions
            })
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            messageRequest.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(messageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"DigitalOcean decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var native = MapDigitalOceanDecisionResponse(body, request);
            var headers = response.GetHeaders();
            var result = new OpenAICompatibleDecisionResult(native, headers, body.Clone())
                .ToDecisionResponse(request, GetIdentifier());
            result.Response!.Id = headers.GetValueOrDefault("x-request-id");
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("DigitalOcean returned an invalid decision response.", exception);
        }
    }

    private static OpenAIDecisionResponse MapDigitalOceanDecisionResponse(JsonElement body, DecisionRequest request)
    {
        var model = body.GetProperty("model").GetString();
        if (string.IsNullOrWhiteSpace(model))
            throw new JsonException("Missing decision model.");
        var answers = body.GetProperty("answers");
        if (answers.ValueKind != JsonValueKind.Object
            || answers.EnumerateObject().Count() != request.Questions.Count
            || answers.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");

        var mapped = new List<OpenAIDecisionAnswer>();
        foreach (var (id, question) in request.Questions)
        {
            var answer = answers.GetProperty(id);
            if (answer.GetProperty("type").GetString() != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException("Answer type does not match question.");
            OpenAIDecisionAnswer item;
            if (question is DecisionBooleanQuestion)
                item = new OpenAIDecisionPredicateAnswer { Probability = ReadDigitalOceanProbability(answer.GetProperty("noul")) };
            else
            {
                var confidence = ReadDigitalOceanProbability(answer.GetProperty("confidence"));
                var distribution = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var property in answer.GetProperty("probabilities").EnumerateObject())
                    if (!distribution.TryAdd(property.Name, ReadDigitalOceanProbability(property.Value)))
                        throw new JsonException("Duplicate probability key.");
                if (question is DecisionChoiceQuestion)
                {
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing selected choice.");
                    if (!distribution.TryGetValue(selected, out var probability) || probability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice must be a maximal option.");
                    item = new OpenAIDecisionChoiceAnswer
                    {
                        Choice = selected, Confidence = confidence,
                        Probabilities = distribution.Select(pair => new OpenAIDecisionChoiceProbability
                        {
                            Value = pair.Key, Probability = pair.Value
                        }).ToArray()
                    };
                }
                else
                {
                    var score = answer.GetProperty("score").GetDouble();
                    var legend = answer.GetProperty("legend");
                    var levels = ((DecisionScoreQuestion)question).Criteria.Count;
                    if (!double.IsFinite(score) || score < 0 || score > levels - 1
                        || legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != levels)
                        throw new JsonException("Invalid score or legend.");
                    for (var index = 0; index < levels; index++)
                        if (legend.GetProperty(index.ToString(CultureInfo.InvariantCulture)).ValueKind != JsonValueKind.String)
                            throw new JsonException("Invalid score legend label.");
                    item = new OpenAIDecisionScoreAnswer
                    {
                        Score = score, Confidence = confidence,
                        Probabilities = distribution.Select(pair => new OpenAIDecisionScoreProbability
                        {
                            Label = pair.Key, Value = double.Parse(pair.Key, CultureInfo.InvariantCulture), Probability = pair.Value
                        }).ToArray()
                    };
                    var expected = distribution.Sum(pair => double.Parse(pair.Key, CultureInfo.InvariantCulture) * pair.Value);
                    if (Math.Abs(score - expected) > 1e-6)
                        throw new JsonException("Score must be the probability-weighted level position.");
                }
            }
            item.Name = id;
            mapped.Add(item);
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0)
            throw new JsonException("Invalid token usage.");
        return new()
        {
            Model = model, Answers = mapped,
            Usage = new() { InputTokens = input, OutputTokens = output }
        };
    }

    private static double ReadDigitalOceanProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Expected a finite probability in [0,1].");
        return number;
    }
}
