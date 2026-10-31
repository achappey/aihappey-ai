using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Venice;

public partial class VeniceProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null)
            throw new NotSupportedException("Venice Decisions does not support safety_identifier.");
        if (request.AdditionalProperties?.Keys.Any(key => key != "images") == true)
            throw new NotSupportedException("Venice Decisions supports only images as an additional OpenAI request field.");

        var images = new JsonArray();
        var state = MapDecisionInput(request.Input, images);
        var typed = new DecisionRequest
        {
            Model = request.Model,
            State = JsonSerializer.SerializeToElement(state)
        };
        if (request.AdditionalProperties?.TryGetValue("images", out var extraImages) == true)
        {
            if (extraImages.ValueKind != JsonValueKind.Array || extraImages.GetArrayLength() == 0)
                throw new ArgumentException("Venice decision images must be a nonempty array.", nameof(request));
            foreach (var image in extraImages.EnumerateArray())
                images.Add(JsonNode.Parse(image.GetRawText()));
        }
        if (images.Count > 0)
            typed.ProviderOptions = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new JsonObject { ["images"] = images })
            };

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            typed.Questions[DecisionIndex(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(
                        DecisionIndex(i), (DecisionInput?)new DecisionInput(
                            JsonSerializer.Serialize(option.Value) + (option.Description is null ? "" : ": " + option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        level.Label + (level.Description is null ? "" : ": " + level.Description))).ToArray()
                },
                _ => throw new NotSupportedException("Unsupported Venice decision question.")
            };
        }

        var result = await DecisionRequestAsync(typed, cancellationToken);
        var body = result.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = DecisionIndex(index);
            var question = request.Questions[index];
            var native = body.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, result.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean)
                    => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice, CultureInfo.InvariantCulture)].Value,
                    Confidence = native.GetProperty("confidence").GetDouble(),
                    Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                    {
                        Value = option.Value,
                        Probability = selected.Probabilities![DecisionIndex(i)]
                    }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => new OpenAIDecisionScoreAnswer
                {
                    Score = scored.Score / (score.Levels.Count - 1d),
                    Confidence = native.GetProperty("confidence").GetDouble(),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    {
                        Label = level.Label,
                        Value = i / (score.Levels.Count - 1d),
                        Probability = scored.Probabilities![DecisionIndex(i)]
                    }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected Venice decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }
        return new()
        {
            Model = body.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = result.Usage!.InputTokens!.Value,
                OutputTokens = result.Usage.OutputTokens!.Value,
                TotalTokens = checked(result.Usage.InputTokens.Value + result.Usage.OutputTokens.Value)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(result.ProviderMetadata, DecisionJsonOptions)
            }
        };
    }

    private static JsonNode MapDecisionInput(OpenAIDecisionInput input, JsonArray images)
    {
        if (input.Text is not null)
            return JsonValue.Create(input.Text)!;
        var messages = new JsonArray();
        foreach (var message in input.Messages!)
        {
            if (message.Role != "user" || message.Phase is not null)
                throw new NotSupportedException("Venice decision SDK state currently supports user messages only.");
            if (message.Content.Text is not null)
            {
                messages.Add(JsonSerializer.SerializeToNode(message, DecisionJsonOptions));
                continue;
            }
            var content = new JsonArray();
            foreach (var part in message.Content.Parts!)
            {
                if (part is not (OpenAIDecisionInputText or OpenAIDecisionInputImage)
                    || part is OpenAIDecisionInputText { PromptCacheBreakpoint: not null }
                    or OpenAIDecisionInputImage { PromptCacheBreakpoint: not null })
                    throw new NotSupportedException("Venice decision SDK state currently supports text and images without cache breakpoints.");
                if (part is OpenAIDecisionInputImage image)
                {
                    if (image.Detail is not (null or "auto"))
                        throw new NotSupportedException("Venice decision images do not support detail settings.");
                    images.Add(JsonValue.Create(image.ImageUrl));
                }
                else
                    content.Add(JsonSerializer.SerializeToNode(part, DecisionJsonOptions));
            }
            if (content.Count > 0)
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
        }
        // Image-only requests still require a state; an empty object is valid native evidence.
        return messages.Count == 0 ? new JsonObject() : messages;
    }
}
