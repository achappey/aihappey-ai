using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        DecisionInput state;
        if (request.Input.Text is not null)
            state = request.Input.Text;
        else
        {
            if (request.Input.Messages!.Any(message => message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true))
                throw new NotSupportedException("OpenRouter Decisions does not document image inference; OpenAI image input parts are not supported.");
            // Preserve boundaries and every text part rather than concatenating messages.
            state = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, OpenRouterDecisionJsonOptions));
        }

        var sdk = new DecisionRequest { Model = request.Model, State = state };
        if (request.AdditionalProperties is { Count: > 0 })
            sdk.ProviderOptions = new() { [GetIdentifier()] = JsonSerializer.SerializeToElement(request.AdditionalProperties, OpenRouterDecisionJsonOptions) };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            sdk.Questions[OpenRouterDecisionQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    // Generated option IDs keep the string "true" distinct from boolean true,
                    // and avoid collisions with arbitrary caller-provided choice strings.
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create(OpenRouterDecisionOptionId(i),
                        (DecisionInput?)new DecisionInput(DescribeOpenRouterDecisionLevel(
                            option.Value.Value.ValueKind == JsonValueKind.String ? option.Value.Value.GetString()! : option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(DescribeOpenRouterDecisionLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
        }

        var normalized = await ExecuteOpenRouterDecisionAsync(sdk, true, cancellationToken);
        var raw = normalized.ProviderMetadata![GetIdentifier()];
        var warnings = normalized.Warnings.ToList();
        warnings.Add(OpenRouterDecisionWarning("OpenRouter does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero. Ordered scores use an evenly spaced [0,1] scale."));
        if (request.SafetyIdentifier is not null)
            warnings.Add(OpenRouterDecisionWarning("OpenRouter Decisions does not support safety_identifier; it was not forwarded or mapped to user.", "safety_identifier"));

        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = OpenRouterDecisionQuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            var confidence = 0d;
            if (question is not OpenAIDecisionPredicateQuestion)
            {
                if (original.TryGetProperty("confidence", out var reportedConfidence))
                    confidence = ReadOpenRouterDecisionProbability(reportedConfidence);
                else
                    warnings.Add(OpenRouterDecisionWarning("OpenRouter omitted confidence; the required OpenAI-compatible confidence field defaults to zero, not an inferred confidence.", id));
                if (!original.TryGetProperty("probabilities", out _))
                    warnings.Add(OpenRouterDecisionWarning("OpenRouter omitted probabilities; the required OpenAI-compatible probabilities list is empty, not an invented distribution.", id));
            }

            OpenAIDecisionAnswer mapped;
            switch (question)
            {
                case OpenAIDecisionPredicateQuestion:
                    mapped = new OpenAIDecisionPredicateAnswer { Probability = ((DecisionBooleanAnswer)normalized.Answers[id]).Probability };
                    break;
                case OpenAIDecisionChoiceQuestion choice:
                    var selected = (DecisionChoiceAnswer)normalized.Answers[id];
                    var selectedIndex = Enumerable.Range(0, choice.Choices.Count).Single(i => OpenRouterDecisionOptionId(i) == selected.Choice);
                    mapped = new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[selectedIndex].Value,
                        Confidence = confidence,
                        Probabilities = selected.Probabilities is null ? [] : choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                        { Value = option.Value, Probability = selected.Probabilities[OpenRouterDecisionOptionId(i)] }).ToArray()
                    };
                    break;
                case OpenAIDecisionScoreQuestion score:
                    var scored = (DecisionScoreAnswer)normalized.Answers[id];
                    // A sole level occupies zero; never divide by zero.
                    var divisor = Math.Max(1, score.Levels.Count - 1);
                    mapped = new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / divisor,
                        Confidence = confidence,
                        Probabilities = scored.Probabilities is null ? [] : score.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
                        { Label = level.Label, Value = i / (double)divisor, Probability = scored.Probabilities[OpenRouterDecisionIndex(i)] }).ToArray()
                    };
                    break;
                default: throw new InvalidOperationException("Unexpected OpenRouter decision question type.");
            }
            mapped.Name = question.Name;
            answers.Add(mapped);
        }

        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new()
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, OpenRouterDecisionJsonOptions),
                ["response"] = JsonSerializer.SerializeToElement(normalized.Response, OpenRouterDecisionJsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(warnings, OpenRouterDecisionJsonOptions)
            }
        };
    }

    private static string OpenRouterDecisionQuestionId(int index) => "question-" + OpenRouterDecisionIndex(index);
    private static string OpenRouterDecisionOptionId(int index) => "option-" + OpenRouterDecisionIndex(index);
    private static string DescribeOpenRouterDecisionLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
