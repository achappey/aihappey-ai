using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(chatRequest);

        var messages = ToRunpodMessages(chatRequest);

        using var doc = await RunSyncAdaptiveAsync(
            model: NormalizeRunpodModelId(chatRequest.Model),
            messages: messages,
            temperature: chatRequest.Temperature,
            maxTokens: chatRequest.MaxOutputTokens,
            topP: chatRequest.TopP,
            topK: null,
            cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var (text, promptTokens, completionTokens) = ExtractRunpodTextAndUsage(root);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return id.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = id,
                Delta = text
            };
            yield return id.ToTextEndUIMessageStreamPart();
        }

        var pt = promptTokens ?? 0;
        var ct = completionTokens ?? 0;
        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: ct,
            inputTokens: pt,
            totalTokens: pt + ct,
            temperature: chatRequest.Temperature);
    }
}
