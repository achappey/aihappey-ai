using AIHappey.Common.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.JigsawStack;

public partial class JigsawStackProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

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

        JigsawExecutionResult? executed = null;
        string? error = null;

        try
        {
            var metadata = chatRequest.GetProviderMetadata<JigsawStackProviderMetadata>(GetIdentifier());
            executed = await ExecuteModelAsync(modelId, texts, metadata, cancellationToken);
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
        yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = executed!.Text };
        yield return id.ToTextEndUIMessageStreamPart();
        yield return "stop".ToFinishUIPart(modelId, 0, 0, 0, chatRequest.Temperature);
    }
}
