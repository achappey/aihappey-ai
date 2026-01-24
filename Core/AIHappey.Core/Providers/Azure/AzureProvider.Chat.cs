using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken)
            ?? throw new ArgumentException(chatRequest.Model);

        switch (model.Type)
        {
            case "transcription":
                {
                    await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            case "speech":

                {
                    await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }

            case "language":

                {
                    await foreach (var p in this.StreamTranslateAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }

            default:
                throw new NotImplementedException();
        }
    }

    private async IAsyncEnumerable<UIMessagePart> StreamTranslateAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var targetLanguage = GetTranslateTargetLanguageFromModel(chatRequest.Model);

        // Translate each incoming text part from the last user message.
        var lastUser = chatRequest.Messages?.LastOrDefault(m => m.Role == Role.user);
        var texts = lastUser?.Parts?.OfType<TextUIPart>()
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? [];

        if (texts.Count == 0)
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        IReadOnlyList<string>? translated = null;
        string? error = null;

        try
        {
            translated = await TranslateAsync(texts, targetLanguage, cancellationToken);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            yield return error!.ToErrorUIPart();
            yield break;
        }

        var id = Guid.NewGuid().ToString("n");
        yield return id.ToTextStartUIMessageStreamPart();

        for (var i = 0; i < translated!.Count; i++)
        {
            var text = translated[i];
            var delta = (i == translated.Count - 1) ? text : (text + "\n");
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = delta };
        }

        yield return id.ToTextEndUIMessageStreamPart();
        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }
}

