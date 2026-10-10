using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeepInfra;

public sealed partial class DeepInfraProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new ArgumentException("DeepInfra Decisions does not support safety_identifier or additional OpenAI request fields.", nameof(request));

        DecisionInput state;
        if (request.Input.Text is not null)
            state = request.Input.Text;
        else
        {
            // DeepInfra documents structured evidence, not OpenAI multimodal inference.
            if (request.Input.Messages!.Any(message => message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true))
                throw new NotSupportedException("DeepInfra Decisions does not currently support OpenAI image input parts.");
            // Keep message boundaries and all text rather than concatenating or discarding content.
            state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions));
        }
        var universal = new DecisionRequest { Model = request.Model, State = state };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            universal.Questions[DeepInfraQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(DeepInfraOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeDeepInfraLevel(
                            option.Value.Value.ValueKind == JsonValueKind.String ? option.Value.Value.GetString()! : option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(DescribeDeepInfraLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var normalized = await DecisionRequestAsync(universal, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = DeepInfraQuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, normalized.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                    { Value = option.Value, Probability = selected.Probabilities![DeepInfraOptionId(i)] }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => new OpenAIDecisionScoreAnswer
                {
                    Score = scored.Score / (score.Levels.Count - 1d),
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                    { Label = level.Label, Value = i / (score.Levels.Count - 1d), Probability = scored.Probabilities![DeepInfraIndex(i)] }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected DeepInfra decision answer type.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }
        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = raw.GetProperty("model").GetString()!, Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, DecisionJsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(new[] { new
                {
                    type = "other", message = "DeepInfra does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero. Score values use an evenly spaced [0,1] scale."
                } }, DecisionJsonOptions)
            }
        };
    }

    private static string DeepInfraQuestionId(int index) => "question-" + DeepInfraIndex(index);
    private static string DeepInfraOptionId(int index) => "option-" + DeepInfraIndex(index);
    private static string DescribeDeepInfraLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
