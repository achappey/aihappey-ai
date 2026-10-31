using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Keln;

public sealed partial class KelnProvider
{
    private void PrepareChat(ChatCompletionOptions request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Headers = SafeHeaders(request.Headers);
        ValidateChatOptions(request.AdditionalProperties);
        ValidateChatOptions(ProviderOptions(request.Metadata));
        request.Metadata = NormalizeTransportOptions(request.Metadata, request.AdditionalProperties, request.Model, stream);
    }

    private void PrepareResponses(ResponseRequest request, bool stream)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        request.Headers = SafeHeaders(request.Headers);
        ValidateResponsesOptions(JsonSerializer.SerializeToElement(request));
        if (ProviderOptions(request.Metadata) is { } options)
            ValidateResponsesOptions(JsonSerializer.SerializeToElement(options));
        if (string.Equals(request.ServiceTier, "standard_only", StringComparison.Ordinal))
            request.ServiceTier = "standard";
        ValidateTier(request.ServiceTier);
        request.Metadata = NormalizeTransportOptions(request.Metadata, request.AdditionalProperties, request.Model, stream);
    }

    private static void ValidateResponsesOptions(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            if ((property.Name is "store" or "background" && property.Value.ValueKind == JsonValueKind.True)
                || (property.Name == "previous_response_id" && property.Value.ValueKind != JsonValueKind.Null)
                || (property.Name == "input" && property.Value.ValueKind == JsonValueKind.Array
                    && property.Value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out var type) && type.GetString() == "item_reference")))
                throw new NotSupportedException("Keln Responses is stateless: store=true, background=true, previous_response_id and item_reference are unsupported.");
            if (property.Name == "service_tier" && property.Value.ValueKind != JsonValueKind.Null)
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Keln service_tier must be standard or lite.");
                ValidateTier(property.Value.GetString());
            }
        }
    }

    private MessagesRequest PrepareMessages(MessagesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Model = NativeModel(request.Model);
        var headers = request.Headers;
        JsonElement? providerOptions = null;
        if (request.Metadata?.AdditionalProperties?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Keln provider options must be an object.");
            providerOptions = options.Clone();
        }
        this.SetDefaultMessagesProperties(request);
        if (providerOptions is { } patch)
        {
            if (patch.TryGetProperty("headers", out var extraHeaders))
            {
                if (extraHeaders.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Keln provider headers must be an object.");
                headers = new Dictionary<string, string>(headers ?? [], StringComparer.OrdinalIgnoreCase);
                foreach (var header in extraHeaders.EnumerateObject())
                    if (IsSafeTransportHeader(header.Name) && header.Value.ValueKind == JsonValueKind.String)
                        headers[header.Name] = header.Value.GetString()!;
            }
            var body = JsonSerializer.SerializeToNode(request)!.AsObject();
            foreach (var property in patch.EnumerateObject())
                if (property.Name is not ("headers" or "tools" or "model" or "stream" or "metadata" or "providerMetadata"))
                    body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            request = body.Deserialize<MessagesRequest>()!;
            request.Headers = headers;
        }
        if (string.Equals(request.ServiceTier, "standard_only", StringComparison.Ordinal))
            request.ServiceTier = "standard";
        ValidateTier(request.ServiceTier);
        ValidateTier(request.AdditionalProperties);
        request.AdditionalProperties?.Remove("model");
        request.AdditionalProperties?.Remove("stream");
        return request;
    }

    private Dictionary<string, object?>? NormalizeTransportOptions(Dictionary<string, object?>? metadata,
        Dictionary<string, JsonElement>? additional, string model, bool stream)
    {
        additional?.Remove("model");
        additional?.Remove("stream");
        var options = ProviderOptions(metadata);
        if (options is null)
            return metadata;
        options["model"] = JsonSerializer.SerializeToElement(model);
        options["stream"] = JsonSerializer.SerializeToElement(stream);
        if (options.TryGetValue("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            options["headers"] = JsonSerializer.SerializeToElement(headers.EnumerateObject()
                .Where(p => IsSafeTransportHeader(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone()));
        return new Dictionary<string, object?>(metadata!) { [GetIdentifier()] = JsonSerializer.SerializeToElement(options) };
    }

    private static bool IsSafeTransportHeader(string name)
        => !ProviderCredentialHeaders.IsProviderKey(name)
            && !name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("host", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string>? SafeHeaders(Dictionary<string, string>? headers)
        => headers?.Where(p => IsSafeTransportHeader(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, JsonElement>? ProviderOptions(Dictionary<string, object?>? metadata)
    {
        if (metadata?.TryGetValue(GetIdentifier(), out var value) != true)
            return null;
        var json = value is JsonElement element ? element : JsonSerializer.SerializeToElement(value);
        if (json.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Keln provider options must be an object.");
        return json.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static void ValidateChatOptions(Dictionary<string, JsonElement>? properties)
    {
        ValidateTier(properties);
        if (properties?.TryGetValue("n", out var n) == true
            && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var count) || count is < 1 or > 8))
            throw new ArgumentOutOfRangeException(nameof(properties), "Keln chat n must be between 1 and 8.");
    }

    private static void ValidateTier(Dictionary<string, JsonElement>? properties)
    {
        if (properties?.TryGetValue("service_tier", out var tier) == true)
        {
            if (tier.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Keln service_tier must be standard or lite.");
            ValidateTier(tier.GetString());
        }
    }

    private static void ValidateTier(string? tier)
    {
        if (tier is not (null or "standard" or "lite"))
            throw new ArgumentException("Keln service_tier must be standard or lite.");
    }
}
