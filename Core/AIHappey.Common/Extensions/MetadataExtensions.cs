using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Common.Extensions;

public static class MetadataExtensions
{

    public static ResponseFormat? GetJSONSchema(this object? structured)
    {
        if (structured == null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ResponseFormat>(JsonSerializer.Serialize(structured));
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<string, object?>? ToObjectDictionary(
           this JsonObject? obj)
    {
        if (obj is null || obj.Count == 0)
            return null;

        var result = new Dictionary<string, object?>(obj.Count);

        foreach (var (key, node) in obj)
        {
            result[key] = node switch
            {
                null => null,

                JsonValue value => value.TryGetValue<bool>(out var b) ? b :
                                   value.TryGetValue<long>(out var l) ? l :
                                   value.TryGetValue<double>(out var d) ? d :
                                   value.TryGetValue<string>(out var s) ? s :
                                   value.GetValue<object?>(),

                JsonObject o => JsonSerializer.SerializeToElement(o),
                JsonArray a => JsonSerializer.SerializeToElement(a),

                _ => JsonSerializer.SerializeToElement(node)
            };
        }

        return result;
    }
    
    public static Dictionary<string, object?> ToObjectDictionary(this object obj)
    {
        var element = JsonSerializer.SerializeToElement(obj, JsonSerializerOptions.Web);

        return element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value)
            : [];
    }


    public static Dictionary<string, object?>? ToObjectDictionary(
        this Dictionary<string, JsonElement>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        var result = new Dictionary<string, object?>(source.Count);

        foreach (var (key, value) in source)
        {
            result[key] = value.ValueKind switch
            {
                JsonValueKind.Object or JsonValueKind.Array => value,
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.TryGetInt64(out var l) ? l :
                                        value.TryGetDouble(out var d) ? d :
                                        value.GetDecimal(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value
            };
        }

        return result;
    }


    public static T? GetRealtimeProviderMetadata<T>(this RealtimeRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderOptions is null)
            return default;

        if (!chatRequest.ProviderOptions.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static T? GetProviderMetadata<T>(this ChatRequest chatRequest, string providerId)
    {
        if (chatRequest.ProviderMetadata is null)
            return default;

        if (!chatRequest.ProviderMetadata.TryGetValue(providerId, out JsonElement element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static T? GetProviderMetadata<T>(this CreateMessageRequestParams chatRequest, string providerId)
    {
        if (chatRequest.Metadata is not JsonObject meta)
            return default;

        if (!meta.TryGetPropertyValue(providerId, out var node))
            return default;

        if (node is null)
            return default;

        return node.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static List<UIMessage> EnsureApprovals(this List<UIMessage> uIMessages) =>
   [.. uIMessages.Select(a =>
            {
                a.Parts = [.. a.Parts.Select(z =>
                {
                    if(z is ToolInvocationPart toolInvocationPart) {
                    if(toolInvocationPart.State == "approval-responded" && toolInvocationPart.Approval?.Approved == false)
                            {
                                toolInvocationPart.Output = toolInvocationPart.Approval;
                            }
                    }
                return z;
                })];

                return a;
            })];

}
