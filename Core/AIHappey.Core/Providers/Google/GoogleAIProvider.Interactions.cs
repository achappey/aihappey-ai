using ModelContextProtocol.Protocol;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Interactions.Extensions;
using AIHappey.Interactions;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async IAsyncEnumerable<InteractionStreamEventPart> GetInteractions(InteractionRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        ApplyAuthHeader();
        request.Stream = true;
        request.Store = false;
        await foreach (var update in _client.GetInteractions(
                           request,
                           ct: cancellationToken))
        {


            yield return update;
        }

    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
          => this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
             CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
        /*  var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

          await foreach (var part in this.StreamUnifiedAsync(
              unifiedRequest,
              cancellationToken))
          {
              foreach (var item in part.ToMessageStreamParts())
                  yield return item;
          }

          yield break;*/
    }

}
