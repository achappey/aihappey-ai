using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
   
    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        var model = await this.GetModel(modelId, cancellationToken: cancellationToken);

        // translate model
        if (model.Type == "language")
        {
            var texts = chatRequest.Messages
                .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
                .SelectMany(m => m.Content.OfType<TextContentBlock>())
                .Select(b => b.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (texts.Count == 0)
                throw new Exception("No prompt provided.");

            var result = await TranslateAsync(texts, modelId?.Split("/").Last()!, cancellationToken);
            var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

            return new CreateMessageResult
            {
                Role = ModelContextProtocol.Protocol.Role.Assistant,
                Model = modelId!,
                StopReason = "stop",
                Content = [joined.ToTextContentBlock()]
            };
        }

        // fallback: speech sampling
        return await this.SpeechSamplingAsync(chatRequest, cancellationToken);
    }
}

