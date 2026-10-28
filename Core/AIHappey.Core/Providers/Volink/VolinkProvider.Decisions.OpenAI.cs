using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Volink;

public partial class VolinkProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new ArgumentException(
                "Volink Decisions does not support safety_identifier or additional OpenAI request fields.",
                nameof(request));

        DecisionInput state;
        if (request.Input.Text is not null)
            state = request.Input.Text;
        else
        {
            if (request.Input.Messages!.Any(message =>
                    message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true))
                throw new NotSupportedException("Volink Decisions does not document image input.");
            state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, VolinkDecisionJsonOptions));
        }

        var normalizedRequest = new DecisionRequest { Model = request.Model, State = state };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            normalizedRequest.Questions[VolinkQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion
                {
                    Instructions = question.Instructions
                },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) =>
                            KeyValuePair.Create(VolinkOptionId(i),
                                (DecisionInput?)new DecisionInput(VolinkDecisionDescription(
                                    option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level =>
                        (DecisionInput?)new DecisionInput(VolinkDecisionDescription(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question.", nameof(request))
            };
        }

        var normalized = await DecisionRequestAsync(normalizedRequest, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = VolinkQuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, normalized.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[int.Parse(
                            selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                        Confidence = original.GetProperty("confidence").GetDouble(),
                        Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                        {
                            Value = option.Value,
                            Probability = selected.Probabilities![VolinkOptionId(i)]
                        }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                    new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / (score.Levels.Count - 1d),
                        Confidence = original.GetProperty("confidence").GetDouble(),
                        Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                        {
                            Label = level.Label,
                            Value = i / (score.Levels.Count - 1d),
                            Probability = scored.Probabilities![VolinkDecisionIndex(i)]
                        }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected Volink decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = normalized.Usage!.InputTokens!.Value;
        var outputTokens = normalized.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, VolinkDecisionJsonOptions)
            }
        };
    }

    private static string VolinkQuestionId(int index) => "question-" + VolinkDecisionIndex(index);
    private static string VolinkOptionId(int index) => "option-" + VolinkDecisionIndex(index);
    private static string VolinkDecisionDescription(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
