using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Swarms;

public partial class SwarmsProvider
{
   public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(chatRequest);

      var modelId = chatRequest.Model;
      ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

      var prompt = BuildPromptFromUiMessages(chatRequest.Messages ?? []);
      if (string.IsNullOrWhiteSpace(prompt))
      {
         yield return "No prompt provided.".ToErrorUIPart();
         yield break;
      }

      var id = Guid.NewGuid().ToString("n");
      yield return id.ToTextStartUIMessageStreamPart();


      await foreach (var delta in ExecuteCompletionStreamingAsync(
                         modelId,
                         prompt,
                         BuildHistoryFromUiMessages(chatRequest.Messages ?? []),
                         ExtractSystemPrompt(chatRequest.Messages ?? []),
                         chatRequest.Temperature,
                         null,
                         cancellationToken))
      {
         yield return new TextDeltaUIMessageStreamPart
         {
            Id = id,
            Delta = delta
         };
      }

      yield return id.ToTextEndUIMessageStreamPart();
      yield return "stop".ToFinishUIPart(modelId, 0, 0, 0, chatRequest.Temperature);

   }
}
