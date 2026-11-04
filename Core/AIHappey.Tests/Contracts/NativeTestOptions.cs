using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;

namespace AIHappey.Tests.Contracts;

/// <summary>Builds native test fixtures from explicit root-level JSON fields.</summary>
internal static class NativeTestOptions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static T Apply<T>(T request, object rootOptions)
    {
        var root = JsonSerializer.SerializeToElement(rootOptions, Json);
        var fields = JsonSerializer.SerializeToElement(request, Json).EnumerateObject()
            .GroupBy(property => property.Name)
            .ToDictionary(group => group.Key, group => group.Last().Value.Clone());
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "headers") continue;
            if (property.Name is "model" or "messages" && fields.ContainsKey(property.Name)) continue;
            if (property.Name == "tools" && fields.TryGetValue("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0) continue;
            fields[property.Name] = property.Value.Clone();
        }
        var result = JsonSerializer.SerializeToElement(fields, Json).Deserialize<T>(Json)!;
        switch (request, result)
        {
            case (ChatCompletionOptions source, ChatCompletionOptions target): target.Headers = source.Headers; break;
            case (MessagesRequest source, MessagesRequest target): target.Headers = source.Headers; break;
            case (ResponseRequest source, ResponseRequest target): target.Headers = source.Headers; break;
        }
        if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            var transportHeaders = headers.Deserialize<Dictionary<string, string>>(Json);
            switch (result)
            {
                case ChatCompletionOptions target: target.Headers = transportHeaders; break;
                case MessagesRequest target: target.Headers = transportHeaders; break;
                case ResponseRequest target: target.Headers = transportHeaders; break;
            }
        }
        return result;
    }
}
