using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("voxtral"))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
    }

    


    private static JsonNode? ToToolArrayNode(IEnumerable<JsonNode> tools)
        => MistralExtensions.ToToolArrayNode(tools);

    private static void AddSerializedToolNode(List<JsonNode> tools, object? tool)
        => MistralExtensions.AddSerializedToolNode(tools, tool);

    private static JsonNode? TryCreateToolNode(JsonElement tool)
        => MistralExtensions.TryCreateToolNode(tool);



    private static object DeserializeToolInput(string input)
        => MistralExtensions.DeserializeToolInput(input);

}
