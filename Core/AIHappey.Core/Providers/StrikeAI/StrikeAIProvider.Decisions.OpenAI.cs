using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StrikeAI;

public sealed partial class StrikeAIProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("StrikeAI Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("StrikeAI Decisions does not support OpenAI image inputs.");

        var universal = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions)),
            ProviderOptions = new()
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new { return_raw_probabilities = true })
            }
        };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            universal.Questions[QuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(OptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeLevel(option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new NotSupportedException($"StrikeAI does not support question type '{question.Type}'.")
            };
        }

        var result = await DecisionRequestAsync(universal, cancellationToken);
        var raw = result.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = QuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, result.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) when selected.Probabilities is not null =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[int.Parse(selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                        Confidence = original.GetProperty("confidence").GetDouble(),
                        Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                        { Value = option.Value, Probability = selected.Probabilities[OptionId(i)] }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) when scored.Probabilities is not null =>
                    new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score,
                        Confidence = original.GetProperty("confidence").GetDouble(),
                        Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                        { Label = level.Label, Value = i, Probability = scored.Probabilities[Index(i)] }).ToArray()
                    },
                _ => throw new InvalidOperationException("StrikeAI omitted the probability distribution required by the OpenAI Decisions contract.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }
        var input = result.Usage!.InputTokens!.Value;
        var output = result.Usage.OutputTokens!.Value;
        return new()
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(result.ProviderMetadata, DecisionJsonOptions),
                ["response"] = JsonSerializer.SerializeToElement(result.Response, DecisionJsonOptions)
            }
        };
    }

    private static string QuestionId(int index) => "question-" + Index(index);
    private static string OptionId(int index) => "option-" + Index(index);
    private static string DescribeLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
