using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SubQ;

/// <summary>OpenAI-shaped decisions request routed to the native /systemone endpoint.</summary>
public sealed partial class SubQProvider
{
    private const string OptionPrefix = "option-";
    private static string OptionId(int index) => OptionPrefix + index.ToString(CultureInfo.InvariantCulture);
    private static string QuestionId(int index) => "question-" + index.ToString(CultureInfo.InvariantCulture);

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("SubQ Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Questions.Count > MaxQuestions)
            throw new ArgumentException($"SubQ supports 1 to {MaxQuestions} decision questions.", nameof(request));

        var state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions));
        var typedRequest = new DecisionRequest { Model = request.Model, State = state };

        for (var index = 0; index < request.Questions.Count; index++)
        {
            typedRequest.Questions[QuestionId(index)] = request.Questions[index] switch
            {
                OpenAIDecisionPredicateQuestion question => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice when choice.Choices.Count is >= 2 and <= 255
                    => new DecisionChoiceQuestion
                    {
                        Instructions = choice.Instructions,
                        Criteria = choice.Choices.Select((option, optionIndex) =>
                                KeyValuePair.Create(OptionId(optionIndex),
                                    (DecisionInput?)new DecisionInput(DescribeLabel(ChoiceLabel(option.Value), option.Description))))
                            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                    },
                OpenAIDecisionChoiceQuestion => throw new ArgumentException(
                    "SubQ choice questions require 2 to 255 options.", nameof(request)),
                OpenAIDecisionScoreQuestion score when score.Levels.Count is >= 2 and <= 10
                    => new DecisionScoreQuestion
                    {
                        Instructions = score.Instructions,
                        Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                            DescribeLabel(level.Label, level.Description))).ToArray()
                    },
                OpenAIDecisionScoreQuestion => throw new ArgumentException(
                    "SubQ score questions require 2 to 10 levels.", nameof(request)),
                _ => throw new NotSupportedException("SubQ Decisions does not support this OpenAI decision question.")
            };
        }

        var typedResponse = await DecisionRequestAsync(typedRequest, cancellationToken);
        var body = typedResponse.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = QuestionId(index);
            var question = request.Questions[index];
            var native = body.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, typedResponse.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean)
                    => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected)
                    => new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[int.Parse(selected.Choice[OptionPrefix.Length..], CultureInfo.InvariantCulture)].Value,
                        Confidence = native.GetProperty("confidence").GetDouble(),
                        Probabilities = choice.Choices.Select((option, optionIndex) => new OpenAIDecisionChoiceProbability
                        {
                            Value = option.Value,
                            Probability = selected.Probabilities![OptionId(optionIndex)]
                        }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored)
                    => new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / (score.Levels.Count - 1d),
                        Confidence = native.GetProperty("confidence").GetDouble(),
                        Probabilities = score.Levels.Select((level, levelIndex) => new OpenAIDecisionScoreProbability
                        {
                            Label = level.Label,
                            Value = levelIndex / (score.Levels.Count - 1d),
                            Probability = scored.Probabilities![levelIndex.ToString(CultureInfo.InvariantCulture)]
                        }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected SubQ decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = typedResponse.Usage?.InputTokens ?? 0;
        var outputTokens = typedResponse.Usage?.OutputTokens ?? 0;
        return new OpenAIDecisionResponse
        {
            Model = StripProviderPrefix(request.Model),
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(typedResponse.ProviderMetadata, DecisionJsonOptions)
            }
        };
    }

    private static string ChoiceLabel(OpenAIDecisionChoiceValue value)
        => value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()! : value.Value.GetRawText();

    private static string DescribeLabel(string label, string? description)
        => string.IsNullOrWhiteSpace(description) ? label : $"{label}: {description}";
}
