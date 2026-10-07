using System.Globalization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static partial class ModelProviderDecisionExtensions
{
    public static DecisionResponse ToDecisionResponse(
        this OpenAICompatibleDecisionResult result, DecisionRequest request, string providerIdentifier)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentifier);
        if (result.Response.Answers is null || result.Response.Answers.Count != request.Questions.Count)
            throw new InvalidOperationException("Decision response must contain exactly one answer per question.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        var index = 0;
        foreach (var (name, question) in request.Questions)
        {
            var answer = result.Response.Answers[index++];
            if (answer is null || answer.Name != name)
                throw new InvalidOperationException("Decision answer names must match question IDs in order.");
            answers[name] = answer is OpenAIDecisionRefusalAnswer ? new DecisionRefusalAnswer()
                : MapDecisionAnswer(question, answer);
        }
        return new DecisionResponse
        {
            Answers = answers,
            Usage = new() { InputTokens = result.Response.Usage.InputTokens, OutputTokens = result.Response.Usage.OutputTokens },
            // Keep the actual provider response, including confidence, detailed usage,
            // and future fields that the typed contract does not yet understand.
            ProviderMetadata = providerIdentifier.CreatePrimitiveProviderMetadata(result.Body.Clone()),
            Response = new()
            {
                ModelId = result.Response.Model.ToModelId(providerIdentifier),
                Timestamp = DateTimeOffset.UtcNow,
                Headers = result.Headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = result.Body.Clone()
            },
            Warnings = []
        };
    }

    private static DecisionAnswer MapDecisionAnswer(DecisionQuestion question, OpenAIDecisionAnswer answer)
    {
        switch (question, answer)
        {
            case (DecisionBooleanQuestion { Criteria: null }, OpenAIDecisionPredicateAnswer predicate):
                ValidateProbability(predicate.Probability);
                return new DecisionBooleanAnswer { Probability = predicate.Probability };
            case (DecisionBooleanQuestion { Criteria: not null }, OpenAIDecisionChoiceAnswer boolean):
                var booleanDistribution = ReadChoiceDistribution(boolean, [new(true), new(false)]);
                return new DecisionBooleanAnswer { Probability = booleanDistribution["boolean:true"] };
            case (DecisionChoiceQuestion choiceQuestion, OpenAIDecisionChoiceAnswer choice):
                var distribution = ReadChoiceDistribution(choice, choiceQuestion.Criteria.Keys.Select(key => new OpenAIDecisionChoiceValue(key)).ToArray());
                return new DecisionChoiceAnswer
                {
                    Choice = choice.Choice.Value.GetString()!,
                    Probabilities = choiceQuestion.Criteria.Keys.ToDictionary(key => key, key => distribution["string:" + key], StringComparer.Ordinal)
                };
            case (DecisionScoreQuestion scoreQuestion, OpenAIDecisionScoreAnswer score):
                if (score.Probabilities is null || score.Probabilities.Count != scoreQuestion.Criteria.Count)
                    throw new InvalidOperationException("Decision score probabilities must cover every level.");
                var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var item in score.Probabilities)
                {
                    ValidateProbability(item.Probability);
                    if (!int.TryParse(item.Label, NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                        || level < 0 || level >= scoreQuestion.Criteria.Count
                        || item.Label != level.ToString(CultureInfo.InvariantCulture)
                        || !probabilities.TryAdd(item.Label, item.Probability))
                        throw new InvalidOperationException("Decision score distribution contains an unknown or duplicate level.");
                }
                ValidateDistribution(probabilities.Values);
                // Native models may use a normalized scale. Vercel requires a weighted
                // position on zero-based level indices, not the provider's score/value scale.
                return new DecisionScoreAnswer
                {
                    Score = probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value),
                    Probabilities = probabilities
                };
            default: throw new InvalidOperationException("Decision answer type does not match the original question.");
        }
    }

    private static Dictionary<string, double> ReadChoiceDistribution(
        OpenAIDecisionChoiceAnswer answer, IReadOnlyList<OpenAIDecisionChoiceValue> choices)
    {
        if (!IsChoiceValue(answer.Choice.Value) || answer.Probabilities is null || answer.Probabilities.Count != choices.Count)
            throw new InvalidOperationException("Decision choice probabilities must cover every supplied choice.");
        var expected = choices.Select(ChoiceKey).ToHashSet(StringComparer.Ordinal);
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in answer.Probabilities)
        {
            ValidateProbability(item.Probability);
            if (!IsChoiceValue(item.Value.Value) || !expected.Contains(ChoiceKey(item.Value))
                || !probabilities.TryAdd(ChoiceKey(item.Value), item.Probability))
                throw new InvalidOperationException("Decision choice distribution contains an unknown or duplicate typed value.");
        }
        ValidateDistribution(probabilities.Values);

        if (!probabilities.ContainsKey(ChoiceKey(answer.Choice)))
            throw new InvalidOperationException("Selected decision choice must exist in the probability distribution.");
        return probabilities;
    }

    private static void ValidateProbability(double probability)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1)
            throw new InvalidOperationException("Decision probabilities must be finite values in [0, 1].");
    }

    private static void ValidateDistribution(IEnumerable<double> probabilities)
    {
        if (Math.Abs(probabilities.Sum() - 1) > 0.000001)
            throw new InvalidOperationException("Decision probabilities must sum to one.");
    }
}
