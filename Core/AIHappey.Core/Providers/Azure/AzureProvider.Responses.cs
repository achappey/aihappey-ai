using AIHappey.Core.AI;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var modelId = options.Model ?? throw new ArgumentException(options.Model);
        var model = await this.GetModel(modelId, cancellationToken);

        if (model.Type == "speech")
            return await this.SpeechResponseAsync(options, cancellationToken);

        if (model.Type == "language")
            return await this.TranslateResponsesAsync(options, cancellationToken);

        throw new NotImplementedException();
    }

    internal async Task<ResponseResult> TranslateResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var now = DateTimeOffset.UtcNow;

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var targetLanguage = GetTranslateTargetLanguageFromModel(modelId);

        var texts = ExtractResponseRequestTexts(options);
        if (texts.Count == 0)
            throw new Exception("No prompt provided.");

        var translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
        var joined = string.Join("\n", translated);

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            CreatedAt = now.ToUnixTimeSeconds(),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Output =
            [
                new
                {
                    type = "message",
                    id = Guid.NewGuid().ToString("n"),
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = joined,
                            annotations = Array.Empty<string>()
                        }
                    }
                }
            ]
        };
    }
}

