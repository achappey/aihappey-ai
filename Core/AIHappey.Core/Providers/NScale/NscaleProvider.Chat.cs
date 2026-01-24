using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.NScale;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Nscale;

public partial class NscaleProvider : IModelProvider
{
    private static readonly JsonSerializerOptions options = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("ByteDance")
          || chatRequest.Model.Contains("stabilityai")
          || chatRequest.Model.Contains("black-forest-labs"))
        {
            await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<NscaleProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = [];

        if (!string.IsNullOrEmpty(metadata?.ReasoningEffort))
        {
            payload["reasoning_effort"] = metadata?.ReasoningEffort;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
