using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);
    private const int DecisionImageLengthLimit = 13_631_488;

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

        var images = new JsonArray();
        var state = sdkState
            ? MapDecisionInput(request.State.Value.Deserialize<OpenAIDecisionInput>(DecisionJsonOptions)!, images)
            : JsonNode.Parse(request.State.Value.GetRawText());
        ValidateDecisionContent(state!, "state");
        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = state,
            ["questions"] = MapDecisionQuestions(request.Questions)
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.venice must be an object.", nameof(request));
            foreach (var option in options.EnumerateObject())
            {
                if (option.Name != "images")
                    throw new NotSupportedException($"Venice Decisions does not support provider option '{option.Name}'; model, state, and questions cannot be overridden.");
                if (option.Value.ValueKind != JsonValueKind.Array || option.Value.GetArrayLength() == 0)
                    throw new ArgumentException("Venice decision images must be a nonempty array.", nameof(request));
                foreach (var image in option.Value.EnumerateArray())
                    images.Add(JsonNode.Parse(image.GetRawText()));
            }
        }
        if (images.Count > 0)
        {
            ValidateDecisionImages(images);
            payload["images"] = images;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Venice decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapDecisionResponse(body, request);
            var headers = response.GetHeaders();
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Venice returned an invalid decision response.", exception);
        }
    }

    private static void ValidateDecisionContent(JsonNode content, string name)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0
            || content is JsonArray { Count: 0 })
            throw new ArgumentException($"Venice decision {name} must not be empty.");
    }

    private static JsonObject MapDecisionQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            var instructions = JsonNode.Parse(question.Instructions!.Value.GetRawText())!;
            ValidateDecisionContent(instructions, $"instructions for '{id}'");
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = instructions
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    var criteria = JsonSerializer.SerializeToNode(boolean.Criteria, DecisionJsonOptions)!.AsObject();
                    if (criteria.Any(item => item.Value is not JsonValue value || !value.TryGetValue<string>(out _)))
                        throw new NotSupportedException("Venice noul criteria must be strings and cannot be null.");
                    mapped["criteria"] = criteria;
                    break;
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    var choices = new JsonObject();
                    foreach (var (key, description) in choice.Criteria)
                        choices[key] = DecisionCriterion(description, true);
                    mapped["criteria"] = choices;
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Count < 2)
                        throw new ArgumentException($"Venice score question '{id}' requires at least two levels.");
                    var levels = new JsonArray();
                    foreach (var level in score.Criteria)
                        levels.Add(DecisionCriterion(level, false));
                    mapped["criteria"] = levels;
                    break;
                default:
                    throw new NotSupportedException($"Venice Decisions does not support question type '{question.Type}'.");
            }
            result[id] = mapped;
        }
        return result;
    }

    private static JsonNode? DecisionCriterion(DecisionInput? input, bool nullable)
    {
        if (input is null && nullable)
            return null;
        if (input is null || input.Value.ValueKind != JsonValueKind.String)
            throw new NotSupportedException("Venice decision criteria must be strings; only choice descriptions may be null.");
        return JsonNode.Parse(input.Value.GetRawText());
    }

    private static void ValidateDecisionImages(JsonArray images)
    {
        if (images.Count > 4)
            throw new ArgumentException("Venice Decisions supports at most four images per request.");
        foreach (var image in images)
        {
            string data;
            if (image is JsonValue value && value.TryGetValue<string>(out var uri))
            {
                if (uri.Length > DecisionImageLengthLimit
                    || !Regex.IsMatch(uri, @"\Adata:image/(?:png|jpeg|jpg);base64,[A-Za-z0-9+/]+=*\z"))
                    throw new ArgumentException("Venice decision images require inline PNG/JPEG data URLs within the 13,631,488-character limit.");
                data = uri[(uri.IndexOf(',') + 1)..];
            }
            else if (image is JsonObject obj && obj.Count == 2
                && obj["content_type"] is JsonValue mime && mime.TryGetValue<string>(out var contentType)
                && contentType is "image/png" or "image/jpeg" or "image/jpg"
                && obj["base64"] is JsonValue bytes && bytes.TryGetValue<string>(out var base64)
                && base64.Length is > 0 and <= DecisionImageLengthLimit
                && Regex.IsMatch(base64, @"\A[A-Za-z0-9+/]+=*\z"))
                data = base64;
            else
                throw new ArgumentException("Venice decision images require a PNG/JPEG data URL or a content_type/base64 object.");
            try { _ = Convert.FromBase64String(data); }
            catch (FormatException exception) { throw new ArgumentException("Venice decision image data must be valid base64.", exception); }
        }
    }

    private static DecisionResponse MapDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString()))
            throw new JsonException("Missing decision model.");
        var native = body.GetProperty("answers");
        if (native.ValueKind != JsonValueKind.Object
            || native.EnumerateObject().Count() != request.Questions.Count
            || native.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = native.GetProperty(id);
            if (answer.GetProperty("type").GetString() != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException("Decision answer type does not match question.");
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = DecisionProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    DecisionProbability(answer.GetProperty("confidence"));
                    var distribution = DecisionDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = answer.GetProperty("choice").GetString();
                    if (selected is null || !distribution.TryGetValue(selected, out var probability)
                        || probability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected decision choice must have a maximal probability.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    DecisionProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(DecisionIndex).ToArray();
                    var probabilities = DecisionDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String
                            || label.GetString() != score.Criteria[int.Parse(key, CultureInfo.InvariantCulture)]!.Value.GetString()))
                        throw new JsonException("Invalid decision score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6)
                        throw new JsonException("Invalid probability-weighted decision score.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
            }
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0 || (long)input + output > int.MaxValue)
            throw new JsonException("Invalid decision usage.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private static string DecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double DecisionProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Decision probability must be in [0,1].");
        return number;
    }

    private static Dictionary<string, double> DecisionDistribution(JsonElement value, IEnumerable<string> keys)
    {
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a decision probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, DecisionProbability(item.Value)))
                throw new JsonException("Unknown or duplicate decision probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized decision distribution.");
        return result;
    }
}
