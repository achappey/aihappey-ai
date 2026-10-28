using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zoom;

public sealed partial class ZoomProvider
{
    private const string DecisionUsageWarning =
        "Zoom meters input in tokens and output in questions, not output tokens. Output, cache-write and reasoning token counters are unavailable and default to zero. Missing native confidence defaults to zero; it is not inferred from probabilities.";

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var result = await DecideAsync(request.ToOpenAIDecisionRequest(GetIdentifier()), request.Headers, cancellationToken);
        var response = result.ToDecisionResponse(request, GetIdentifier());
        response.Response!.Id = RequiredString(result.Body, "request_id");
        response.Warnings = [new { type = "other", message = DecisionUsageWarning }];
        return response;
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        => (await DecideAsync(request, null, cancellationToken)).Response;

    private async Task<OpenAICompatibleDecisionResult> DecideAsync(
        OpenAIDecisionRequest request, IEnumerable<KeyValuePair<string, string?>>? headers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        var model = NativeModel(request.Model);
        if (model is not ("zoom-decisions" or "zoom-decisions-pro"))
            throw new NotSupportedException($"Zoom Decisions does not support model '{request.Model}'.");
        if (request.Questions.Count > 10)
            throw new ArgumentException("Zoom Decisions supports 1 to 10 independent questions.");
        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("Zoom Decisions does not support safety_identifier; use reference_id for correlation.");

        var options = JsonSerializer.SerializeToNode(request.AdditionalProperties ?? [], Json)!.AsObject();
        RequireOnly(options, "enable_image_input", "reference_id");
        if (options.ContainsKey("enable_image_input")
            && (options["enable_image_input"] is not JsonValue flag || !flag.TryGetValue<bool>(out _)))
            throw new ArgumentException("enable_image_input must be a boolean.");
        if (options.ContainsKey("reference_id")
            && (options["reference_id"] is not JsonValue referenceValue || !referenceValue.TryGetValue<string>(out _)))
            throw new ArgumentException("reference_id must be a string.");

        ValidateZoomInput(request.Input);
        var hasImages = request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true;
        if (hasImages && options["enable_image_input"]?.GetValue<bool>() != true)
            throw new ArgumentException("Zoom Decisions image input requires enable_image_input: true.");

        var preserveNames = request.Questions.All(question => !string.IsNullOrWhiteSpace(question.Name))
            && request.Questions.Select(question => question.Name).Distinct(StringComparer.Ordinal).Count() == request.Questions.Count;
        var names = request.Questions.Select((question, index) =>
            preserveNames ? question.Name! : $"question-{index}").ToArray();
        var questions = new JsonArray();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            switch (question)
            {
                case OpenAIDecisionChoiceQuestion choice:
                    if (choice.Choices.Count < 2
                        || choice.Choices.Any(item => string.IsNullOrWhiteSpace(ChoiceKey(item.Value.Value)))
                        || choice.Choices.Select(item => ChoiceKey(item.Value.Value)).Distinct(StringComparer.Ordinal).Count() != choice.Choices.Count)
                        throw new ArgumentException("Zoom choices require at least two unique, nonempty values, without Boolean/string collisions.");
                    break;
                case OpenAIDecisionScoreQuestion score when score.Levels.Count < 2:
                    throw new ArgumentException("Zoom scores require at least two ordered levels.");
            }
            var mapped = JsonSerializer.SerializeToNode(question, Json)!.AsObject();
            mapped["name"] = names[index];
            questions.Add(mapped);
        }
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = JsonSerializer.SerializeToNode(request.Input, Json),
            ["questions"] = questions
        };
        foreach (var option in options)
            payload[option.Key] = option.Value?.DeepClone();

        var (body, responseHeaders) = await SendAsync("aiservices/decisions/decide", payload, true, headers, cancellationToken);
        try
        {
            var response = MapDecisions(body, request, names, model);
            response.AdditionalProperties = new()
            {
                ["request_id"] = body.GetProperty("request_id").Clone(),
                ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [GetIdentifier()] = body }, Json),
                ["warnings"] = JsonSerializer.SerializeToElement(new[] { new { type = "other", message = DecisionUsageWarning } }, Json)
            };
            return new(response, responseHeaders, body);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Zoom returned an invalid Decisions response.", exception);
        }
    }

    private static void ValidateZoomInput(OpenAIDecisionInput input)
    {
        if (input.Text is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
            return;
        }
        foreach (var message in input.Messages!)
        {
            if (message.Content.Text is { } text)
                ArgumentException.ThrowIfNullOrWhiteSpace(text);
            foreach (var part in message.Content.Parts ?? [])
                if (part is OpenAIDecisionInputText textPart)
                    ArgumentException.ThrowIfNullOrWhiteSpace(textPart.Text);
        }
    }

    private static OpenAIDecisionResponse MapDecisions(
        JsonElement body, OpenAIDecisionRequest request, string[] names, string model)
    {
        RequiredString(body, "request_id");
        if (RequiredString(body, "model") != model)
            throw new JsonException("Decision response model does not match the request.");
        var rawAnswers = body.GetProperty("answers");
        if (rawAnswers.ValueKind != JsonValueKind.Array || rawAnswers.GetArrayLength() != request.Questions.Count)
            throw new JsonException("Zoom must return one ordered answer per question.");
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var raw = rawAnswers[index];
            var question = request.Questions[index];
            var type = RequiredString(raw, "type");
            if (RequiredString(raw, "name") != names[index] || (type != "refusal" && type != question.Type))
                throw new JsonException("Zoom answer names and types must match the questions in order.");

            OpenAIDecisionAnswer answer = type == "refusal" ? new OpenAIDecisionRefusalAnswer() : question switch
            {
                OpenAIDecisionPredicateQuestion => new OpenAIDecisionPredicateAnswer
                {
                    Probability = Probability(raw.GetProperty("probability"))
                },
                OpenAIDecisionChoiceQuestion choice => MapChoice(raw, choice),
                OpenAIDecisionScoreQuestion score => MapScore(raw, score),
                _ => throw new JsonException("Unsupported Zoom answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var usage = body.GetProperty("usage");
        if (RequiredString(usage, "unit_type") != "tokens"
            || NonnegativeInteger(usage, "output_units") != request.Questions.Count)
            throw new JsonException("Zoom decision usage must report tokens and the submitted question count.");
        var inputTokens = NonnegativeInteger(usage, "input_units");
        var cached = usage.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_input_tokens", out _) ? NonnegativeInteger(details, "cached_input_tokens") : 0;
        if (cached > inputTokens)
            throw new JsonException("Cached tokens cannot exceed total input tokens.");
        return new()
        {
            Model = model,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                InputTokensDetails = new() { CachedTokens = cached },
                OutputTokens = 0,
                TotalTokens = inputTokens
            }
        };
    }

    private static OpenAIDecisionChoiceAnswer MapChoice(JsonElement raw, OpenAIDecisionChoiceQuestion question)
    {
        var choices = question.Choices.ToDictionary(choice => ChoiceKey(choice.Value.Value), choice => choice.Value, StringComparer.Ordinal);
        var selectedKey = ChoiceKey(raw.GetProperty("choice"));
        if (!choices.TryGetValue(selectedKey, out var selected))
            throw new JsonException("Zoom selected an unknown choice.");
        var probabilities = new List<OpenAIDecisionChoiceProbability>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw.GetProperty("probabilities").EnumerateArray())
        {
            var key = ChoiceKey(item.GetProperty("value"));
            if (!choices.TryGetValue(key, out var value) || !seen.Add(key))
                throw new JsonException("Zoom choice probabilities contain unknown or duplicate values.");
            probabilities.Add(new() { Value = value, Probability = Probability(item.GetProperty("probability")) });
        }
        ValidateDistribution(probabilities.Select(item => item.Probability), probabilities.Count, choices.Count);
        var selectedProbability = probabilities.Single(item => ChoiceKey(item.Value.Value) == selectedKey).Probability;
        if (selectedProbability + 1e-6 < probabilities.Max(item => item.Probability))
            throw new JsonException("Zoom selected choice must have maximal probability.");
        return new()
        {
            Choice = selected,
            Confidence = raw.TryGetProperty("confidence", out var confidence) ? Probability(confidence) : 0,
            Probabilities = probabilities
        };
    }

    private static OpenAIDecisionScoreAnswer MapScore(JsonElement raw, OpenAIDecisionScoreQuestion question)
    {
        var probabilities = new List<OpenAIDecisionScoreProbability>();
        var seen = new HashSet<int>();
        foreach (var item in raw.GetProperty("probabilities").EnumerateArray())
        {
            var value = item.GetProperty("value").GetDouble();
            if (!double.IsFinite(value) || value < 0 || value >= question.Levels.Count || value != Math.Truncate(value))
                throw new JsonException("Zoom score probabilities require zero-based level indices.");
            var index = (int)value;
            if (!seen.Add(index)
                || (item.TryGetProperty("label", out var label) && label.GetString() != question.Levels[index].Label))
                throw new JsonException("Zoom score probabilities contain an invalid level.");
            probabilities.Add(new()
            {
                Label = question.Levels[index].Label,
                Value = index / (question.Levels.Count - 1d),
                Probability = Probability(item.GetProperty("probability"))
            });
        }
        ValidateDistribution(probabilities.Select(item => item.Probability), probabilities.Count, question.Levels.Count);
        var score = raw.GetProperty("score").GetDouble();
        var weighted = probabilities.Sum(item => item.Value * (question.Levels.Count - 1d) * item.Probability);
        if (!double.IsFinite(score) || Math.Abs(score - weighted) > 1e-6)
            throw new JsonException("Zoom score must equal the weighted zero-based level position.");
        return new()
        {
            Score = score / (question.Levels.Count - 1d),
            Confidence = raw.TryGetProperty("confidence", out var confidence) ? Probability(confidence) : 0,
            Probabilities = probabilities
        };
    }

    private static string ChoiceKey(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new JsonException("Zoom choice values must be strings or Booleans.")
    };

    private static double Probability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Zoom probabilities must be finite values in [0, 1].");
        return number;
    }

    private static void ValidateDistribution(IEnumerable<double> probabilities, int count, int expected)
    {
        if (count != expected || Math.Abs(probabilities.Sum() - 1) > 1e-6)
            throw new JsonException("Zoom probability distributions must cover every option and sum to one.");
    }
}
