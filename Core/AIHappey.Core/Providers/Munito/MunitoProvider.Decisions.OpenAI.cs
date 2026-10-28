using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Munito;

/// <summary>POST /decisions: Munito implements OpenAI's Decisions contract natively.</summary>
public sealed partial class MunitoProvider
{
    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateMunitoOpenAIDecisionRequest(request);
        ApplyAuthHeader();

        return await _client.OpenAICompatibleDecisionRequestAsync(
            request,
            endpoint: "decisions",
            cancellationToken: cancellationToken);
    }

    private static void ValidateMunitoOpenAIDecisionRequest(OpenAIDecisionRequest request)
    {
        if (request.Questions.Count > MaxQuestions)
            throw new ArgumentException($"Munito supports 1 to {MaxQuestions} decision questions.", nameof(request));
        if (request.SafetyIdentifier is { Length: > 128 })
            throw new ArgumentException("safety_identifier must be at most 128 characters.", nameof(request));
        if (request.AdditionalProperties?.Count > 0)
            throw new ArgumentException(
                $"Munito Decisions does not support additional request fields ({string.Join(", ", request.AdditionalProperties.Keys)}).",
                nameof(request));

        foreach (var question in request.Questions)
        {
            switch (question)
            {
                case OpenAIDecisionChoiceQuestion { Choices.Count: < 2 or > 255 }:
                    throw new ArgumentException("Munito choice questions require 2 to 255 choices.", nameof(request));
                case OpenAIDecisionScoreQuestion { Levels.Count: < 2 or > 10 }:
                    throw new ArgumentException("Munito score questions require 2 to 10 levels.", nameof(request));
            }
        }

        var images = request.Input.Messages?
            .Where(message => message.Content?.Parts is not null)
            .SelectMany(message => message.Content!.Parts!)
            .OfType<OpenAIDecisionInputImage>()
            .Count() ?? 0;
        if (images == 0)
            return;
        if (IsTextOnlyDecisionModel(request.Model))
            throw new ArgumentException("blockbrain/cygnet is text only and does not accept images.", nameof(request));
        if (images > MaxImages)
            throw new ArgumentException($"Munito Decisions accepts at most {MaxImages} images.", nameof(request));
    }
}
