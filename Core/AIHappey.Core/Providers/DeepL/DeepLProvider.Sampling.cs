using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{

    public async Task<CreateMessageResult> SamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = chatRequest.Messages
            .Where(m => m.Role == Role.User)
            .SelectMany(m => m.Content.OfType<TextContentBlock>())
            .Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await ProcessTextsAsync(texts, modelId, cancellationToken);

        return new CreateMessageResult
        {
            Role = Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [.. translated.Select(a => a.ToTextContentBlock())]
        };
    }
}
