using System.Globalization;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Perplexity;

public partial class PerplexityProvider
{
    /// <summary>Keep the gateway's OpenAI contract, not Perplexity's native wire format.</summary>
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new ArgumentException("Perplexity Decisions does not support safety_identifier or additional OpenAI request fields.");
        var universal = new DecisionRequest { Model = request.Model,
            State = new DecisionInput(JsonSerializer.SerializeToElement(request.Input, JsonOptions)) };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            // Names may be absent or repeated. Internal names are always unique and reversible.
            var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
            universal.Questions[id] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion { Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, i) => KeyValuePair.Create("option-" + i.ToString(CultureInfo.InvariantCulture),
                        (DecisionInput?)new DecisionInput(Describe(option.Value.Value.ValueKind == JsonValueKind.String ? option.Value.Value.GetString()! : option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion { Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(Describe(level.Label, level.Description))).ToArray() },
                _ => throw new ArgumentException("Unsupported decision question type.")
            };
        }
        var normalized = await DecisionRequestAsync(universal, cancellationToken);
        var raw = normalized.ProviderMetadata![Provider];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
            var question = request.Questions[index];
            var answer = normalized.Answers[id];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer mapped = (question, answer) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice["option-".Length..], CultureInfo.InvariantCulture)].Value,
                    Confidence = original.GetProperty("confidence").GetDouble(),
                    Probabilities = choice.Choices.Select((option, i) => new OpenAIDecisionChoiceProbability
                    { Value = option.Value, Probability = selected.Probabilities!["option-" + i.ToString(CultureInfo.InvariantCulture)] }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) => MapOpenAIScore(score, scored, original),
                _ => throw new InvalidOperationException("Unexpected Perplexity answer type.")
            };
            mapped.Name = question.Name;
            answers.Add(mapped);
        }
        var input = normalized.Usage!.InputTokens!.Value;
        var output = normalized.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse { Model = raw.GetProperty("model").GetString()!, Answers = answers,
            Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = checked(input + output) },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(normalized.ProviderMetadata, JsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(normalized.Warnings.Append(new
                {
                    type = "other", message = "Perplexity does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero. Score values use an evenly spaced [0,1] scale."
                }), JsonOptions)
            } };
    }

    private static OpenAIDecisionScoreAnswer MapOpenAIScore(OpenAIDecisionScoreQuestion question, DecisionScoreAnswer answer, JsonElement raw)
    {
        // The native OpenAI compatibility representation uses normalized level values;
        // universal scores remain positions on zero-based indices.
        var denominator = question.Levels.Count - 1d;
        return new OpenAIDecisionScoreAnswer { Score = answer.Score / denominator,
            Confidence = raw.GetProperty("confidence").GetDouble(),
            Probabilities = question.Levels.Select((level, i) => new OpenAIDecisionScoreProbability
            { Label = level.Label, Value = i / denominator, Probability = answer.Probabilities![i.ToString(CultureInfo.InvariantCulture)] }).ToArray() };
    }
    private static string Describe(string label, string? description) => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
