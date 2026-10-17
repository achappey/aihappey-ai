using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Foundry;

public partial class FoundryProvider
{
    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new ArgumentException("Foundry Decisions does not support safety_identifier or additional OpenAI request fields.", nameof(request));

        DecisionInput state;
        if (request.Input.Text is not null)
            state = request.Input.Text;
        else
        {
            if (request.Input.Messages!.Any(message => message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true))
                throw new NotSupportedException("Microsoft-Decision-1 in Foundry does not support OpenAI image input parts.");
            state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, FoundryDecisionJsonOptions));
        }

        var universal = new DecisionRequest { Model = request.Model, State = state };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            universal.Questions[FoundryQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(FoundryOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeFoundryLevel(
                            option.Value.Value.GetRawText(),
                            option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeFoundryLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var normalized = await DecisionRequestAsync(universal, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = FoundryQuestionId(index);
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
                    { Value = option.Value, Probability = selected.Probabilities![FoundryOptionId(i)] }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => new OpenAIDecisionScoreAnswer
                {
                    Score = scored.Score,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    {
                        Label = level.Label,
                        Value = i,
                        Probability = scored.Probabilities![FoundryDecisionIndex(i)]
                    }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected Microsoft Foundry decision answer type.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }

        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, FoundryDecisionJsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    type = "other",
                    message = "Microsoft-Decision-1 in Foundry accepts text and structured JSON only; image inputs and OpenAI-only token detail counters are unavailable."
                } }, FoundryDecisionJsonOptions)
            }
        };
    }

    private static string FoundryQuestionId(int index) => "question-" + FoundryDecisionIndex(index);
    private static string FoundryOptionId(int index) => "option-" + FoundryDecisionIndex(index);
    private static string DescribeFoundryLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
