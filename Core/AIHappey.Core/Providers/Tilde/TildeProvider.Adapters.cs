using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tilde;

public sealed partial class TildeProvider
{
    private AIRequest ToTildeUnifiedRequest(ResponseRequest request)
        => NormalizeNativeOptions(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties);

    private AIRequest ToTildeUnifiedRequest(MessagesRequest request)
    {
        if (request.OutputConfig?.Format is not null)
            throw new NotSupportedException("Tilde supports text translation, not response formats.");

        return NormalizeNativeOptions(request.ToUnifiedRequest(GetIdentifier()), request.AdditionalProperties,
            request.Metadata?.AdditionalProperties);
    }

    private static AIRequest NormalizeNativeOptions(AIRequest request,
        Dictionary<string, JsonElement>? additional = null,
        Dictionary<string, JsonElement>? messageMetadata = null)
    {
        var metadata = request.Metadata is null ? new Dictionary<string, object?>() : new(request.Metadata);
        var native = new Dictionary<string, JsonElement>();
        if (metadata.TryGetValue("messages.request.unmapped", out var unmapped) && unmapped is not null)
            MergeRawOptions(JsonSerializer.SerializeToElement(unmapped));
        if (additional is not null)
            MergeRawOptions(JsonSerializer.SerializeToElement(additional));
        if (messageMetadata?.TryGetValue("tilde", out var messageOptions) == true)
            MergeNativeOptions(messageOptions);
        if (metadata.TryGetValue("tilde", out var scoped) && scoped is not null)
            MergeNativeOptions(JsonSerializer.SerializeToElement(scoped));
        if (native.Count > 0)
            metadata["tilde"] = JsonSerializer.SerializeToElement(native);

        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input, Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens, MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream, ParallelToolCalls = request.ParallelToolCalls, ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat, Tools = request.Tools, Headers = request.Headers,
            Verbosity = request.Verbosity, Metadata = metadata
        };

        void MergeRawOptions(JsonElement raw)
        {
            if (raw.ValueKind != JsonValueKind.Object)
                return;
            foreach (var property in raw.EnumerateObject())
                if (property.Name is not ("providerOptions" or "tilde"))
                    native[property.Name] = property.Value.Clone();
            if (raw.TryGetProperty("tilde", out var tilde))
                MergeNativeOptions(tilde);
            if (raw.TryGetProperty("providerOptions", out var providers))
            {
                if (providers.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("providerOptions must be an object.");
                if (providers.TryGetProperty("tilde", out var options))
                    MergeNativeOptions(options);
            }
        }

        void MergeNativeOptions(JsonElement options)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Tilde provider options must be an object.");
            foreach (var property in options.EnumerateObject())
                native[property.Name] = property.Value.Clone();
        }
    }
}
