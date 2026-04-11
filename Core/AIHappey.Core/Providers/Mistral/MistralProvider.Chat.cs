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
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            array.Add(tool.DeepClone());
        }

        return array.Count == 0 ? null : array;
    }

    private static void AddSerializedToolNode(List<JsonNode> tools, object? tool)
    {
        if (tool is null)
            return;

        var node = JsonSerializer.SerializeToNode(tool, MistralJsonSerializerOptions);
        if (node is not null)
            tools.Add(node);
    }

    private static JsonNode? TryCreateToolNode(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object)
            return null;

        if (!tool.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(typeElement.GetString()))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(tool.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }



    private static object DeserializeToolInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(input, MistralJsonSerializerOptions) ?? new { };
        }
        catch (JsonException)
        {
            return input;
        }
    }

}
