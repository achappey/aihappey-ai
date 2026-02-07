using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var modelId = chatRequest.GetModel();

        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var model = await this.GetModel(modelId, cancellationToken: cancellationToken)
        ?? throw new ArgumentException(modelId);

        return model.Type switch
        {
            "speech" => await this.SpeechSamplingAsync(chatRequest, cancellationToken),
            "language" => await this.TranslateSamplingAsync(chatRequest, modelId!, cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    internal async Task<CreateMessageResult> TranslateSamplingAsync(
        CreateMessageRequestParams chatRequest,
        string modelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);

        var texts = chatRequest.Messages
            .Where(m => m.Role == Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
        var joined = string.Join("\n", translated);

        return new CreateMessageResult
        {
            Role = Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [joined.ToTextContentBlock()]
        };
    }
}

