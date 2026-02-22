using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

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
            translated = await TranslateTextsFromModelAsync(chatRequest.Model, texts, cancellationToken);
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

        for (var i = 0; i < translated!.Count; i++)
        {
            var id = Guid.NewGuid().ToString("n");
            yield return id.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = translated[i] };
            yield return id.ToTextEndUIMessageStreamPart();
        }

        yield return "stop".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
    }

}

