using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SerenityEdge;

public sealed partial class SerenityEdgeProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        => (await SendDecisionRequestAsync(request, cancellationToken)).Response;

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var nativeRequest = request.ToOpenAIDecisionRequest(GetIdentifier());
        var response = await SendDecisionRequestAsync(nativeRequest, cancellationToken);
        NormalizeScoreLabelsForGateway(request, response.Response);
        return response.ToDecisionResponse(request, GetIdentifier());
    }

    private static void NormalizeScoreLabelsForGateway(DecisionRequest request, OpenAIDecisionResponse response)
    {
        if (response.Answers is null || response.Answers.Count != request.Questions.Count)
            throw new InvalidOperationException("Serenity Edge must return exactly one ordered answer per question.");

        var questionIndex = 0;
        foreach (var question in request.Questions.Values)
        {
            if (question is DecisionScoreQuestion score
                && response.Answers[questionIndex] is OpenAIDecisionScoreAnswer answer)
            {
                if (answer.Probabilities is null)
                    throw new InvalidOperationException("Serenity Edge returned no score-level probabilities.");
                foreach (var probability in answer.Probabilities)
                {
                    if (int.TryParse(probability.Label, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var level)
                        && level >= 0 && level < score.Criteria.Count)
                        continue;
                    if (!double.IsFinite(probability.Value)
                        || probability.Value < 0
                        || probability.Value >= score.Criteria.Count
                        || probability.Value != Math.Truncate(probability.Value))
                        throw new InvalidOperationException("Serenity Edge returned an invalid score-level probability.");
                    probability.Label = ((int)probability.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            questionIndex++;
        }
    }

    private async Task<OpenAICompatibleDecisionResult> SendDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateDecisionLimits(request);
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, DecisionJsonOptions),
                System.Text.Encoding.UTF8, System.Net.Mime.MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, body, "Decision");

        try
        {
            using var document = JsonDocument.Parse(body);
            var result = JsonSerializer.Deserialize<OpenAIDecisionResponse>(body, DecisionJsonOptions)
                ?? throw new JsonException("The response body was empty.");
            ValidateDecisionResponse(request, document.RootElement, result);
            return new(result, response.GetHeaders(), document.RootElement.Clone());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Serenity Edge returned an invalid Decisions response.", exception);
        }
    }

    private static void ValidateDecisionResponse(
        OpenAIDecisionRequest request, JsonElement body, OpenAIDecisionResponse response)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Array
            || answers.GetArrayLength() != request.Questions.Count
            || !body.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !body.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("input_tokens", out var inputTokens)
            || inputTokens.ValueKind != JsonValueKind.Number
            || !inputTokens.TryGetInt32(out var inputCount)
            || inputCount < 0
            || !usage.TryGetProperty("output_tokens", out var outputTokens)
            || outputTokens.ValueKind != JsonValueKind.Number
            || !outputTokens.TryGetInt32(out var outputCount)
            || outputCount != 0
            || !usage.TryGetProperty("total_tokens", out var totalTokens)
            || totalTokens.ValueKind != JsonValueKind.Number
            || !totalTokens.TryGetInt32(out var totalCount)
            || totalCount != inputCount
            || !usage.TryGetProperty("input_tokens_details", out var inputDetails)
            || inputDetails.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("output_tokens_details", out var outputDetails)
            || outputDetails.ValueKind != JsonValueKind.Object)
            throw new JsonException("The response did not match the documented Decisions response schema.");

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            var answer = response.Answers[index];
            if (answer is null || answer.Name != question.Name
                || (answer is not OpenAIDecisionRefusalAnswer && answer.Type != question.Type))
                throw new JsonException("Decision answers must match the submitted question types and names in order.");
        }
    }

    private static void ValidateDecisionLimits(OpenAIDecisionRequest request)
    {
        if (request.Questions.Count > 256)
            throw new ArgumentException("Serenity Edge Decisions supports at most 256 questions.", nameof(request));
        foreach (var question in request.Questions)
        {
            if (question is OpenAIDecisionChoiceQuestion { Choices.Count: < 2 })
                throw new ArgumentException("Serenity Edge choice questions require at least two choices.", nameof(request));
            if (question is OpenAIDecisionScoreQuestion score && score.Levels.Count is < 2 or > 10)
                throw new ArgumentException("Serenity Edge score questions require between 2 and 10 levels.", nameof(request));
        }

        var imagesPresent = request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true;
        if (imagesPresent)
            throw new NotSupportedException("Serenity Edge Decisions does not support image input.");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Serenity Edge {operation} request failed with status {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
    }
}
