using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DigitalOcean;

public partial class DigitalOceanProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("DigitalOcean Decisions does not support safety_identifier or additional OpenAI request fields, including streaming.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("DigitalOcean System One does not support image inputs.");

        var typed = new DecisionRequest
        {
            Model = request.Model,
            // System One accepts arrays of text, not arrays of message objects.
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(new { messages = request.Input }))
        };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            typed.Questions[DigitalOceanQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(DigitalOceanOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeDigitalOceanLevel(
                            option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeDigitalOceanLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var normalized = await DecisionRequestAsync(typed, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = DigitalOceanQuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, normalized.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                    {
                        Value = option.Value, Probability = selected.Probabilities![DigitalOceanOptionId(i)]
                    }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => new OpenAIDecisionScoreAnswer
                {
                    Score = scored.Score,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    {
                        Label = level.Label, Value = i,
                        Probability = scored.Probabilities![i.ToString(CultureInfo.InvariantCulture)]
                    }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected DigitalOcean decision answer type.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }
        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new()
        {
            Model = raw.GetProperty("model").GetString()!, Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata),
                ["warnings"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    type = "other",
                    message = "DigitalOcean does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero. Scores use zero-based level positions."
                } })
            }
        };
    }

    private static string DigitalOceanQuestionId(int index) => "question-" + index.ToString(CultureInfo.InvariantCulture);
    private static string DigitalOceanOptionId(int index) => "option-" + index.ToString(CultureInfo.InvariantCulture);
    private static string DescribeDigitalOceanLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
