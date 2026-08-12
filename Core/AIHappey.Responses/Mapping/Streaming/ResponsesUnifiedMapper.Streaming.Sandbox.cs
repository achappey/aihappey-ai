using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static IEnumerable<AIEventEnvelope> CreateSandboxResultEnvelopes(
        string providerId,
        ResponseOutputItemDone done)
    {
        var id = done.Item.CallId ?? done.Item.Id ?? $"sandbox:{done.OutputIndex}";
        var code = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "code")?.ToString() ?? string.Empty;
        var language = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "language")?.ToString() ?? string.Empty;
        var input = new { language, code };

        yield return CreateToolInputStartEnvelope(
            id,
            "sandbox",
            "sandbox",
            providerExecuted: true,
            providerMetadata: CreateSandboxProviderMetadata(providerId, done.Item, done.OutputIndex));

        yield return CreateToolInputEndEnvelope(
            id,
            "sandbox",
            input,
            "sandbox",
            providerExecuted: true,
            providerMetadata: CreateSandboxProviderMetadata(providerId, done.Item, done.OutputIndex));

        yield return CreateToolOutputEnvelope(
            id,
            new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    container_id = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "container_id"),
                    status = done.Item.Status,
                    results = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "results")
                })
            },
            toolName: "sandbox",
            providerExecuted: true,
            providerMetadata: CreateSandboxProviderMetadata(providerId, done.Item, done.OutputIndex));
    }

    private static IEnumerable<AIEventEnvelope> CreateSandboxWriteFileEnvelopes(
        string providerId,
        ResponseOutputItemDone done)
    {
        var id = done.Item.CallId ?? done.Item.Id ?? $"sandbox-write:{done.OutputIndex}";
        var filePath = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "file_path")?.ToString() ?? string.Empty;
        var sizeBytes = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "size_bytes");
        var value = new { file_path = filePath, size_bytes = sizeBytes };
        var metadata = CreateSandboxProviderMetadata(providerId, done.Item, done.OutputIndex);

        yield return CreateToolInputStartEnvelope(
            id, "sandbox", "sandbox write file", providerExecuted: true, providerMetadata: metadata);
        yield return CreateToolInputEndEnvelope(
            id, "sandbox", value, "sandbox write file", providerExecuted: true, providerMetadata: metadata);
        yield return CreateToolOutputEnvelope(
            id, value, toolName: "sandbox", providerExecuted: true, providerMetadata: metadata);
    }

    private static AIEventEnvelope? CreateSharedFileEnvelope(string providerId, ResponseOutputItemDone done)
    {
        var dataUrl = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "file_data")?.ToString();
        if (string.IsNullOrWhiteSpace(dataUrl))
            return null;

        var mediaType = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "media_type")?.ToString()
            ?? "application/octet-stream";
        var filename = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "filename")?.ToString();
        var fileId = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "file_id")?.ToString();
        var id = done.Item.CallId ?? fileId ?? $"share-file:{done.OutputIndex}";

        return CreateFileEnvelope(
            id,
            mediaType,
            dataUrl,
            filename,
            CreateProviderMetadata(providerId, new Dictionary<string, object?>
            {
                ["type"] = "share_file",
                ["call_id"] = done.Item.CallId,
                ["file_id"] = fileId,
                ["filename"] = filename,
                ["size_bytes"] = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "size_bytes"),
                ["response_id"] = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "response_id"),
                ["source"] = "share_file"
            }));
    }

    private static Dictionary<string, Dictionary<string, object>> CreateSandboxProviderMetadata(
        string providerId,
        ResponseStreamItem item,
        int outputIndex)
        => CreateProviderMetadata(providerId, new Dictionary<string, object?>
        {
            ["type"] = item.Type,
            ["call_id"] = item.CallId,
            ["status"] = item.Status,
            ["output_index"] = outputIndex,
            ["container_id"] = GetAdditionalPropertyValue(item.AdditionalProperties, "container_id"),
            ["language"] = GetAdditionalPropertyValue(item.AdditionalProperties, "language"),
            ["file_path"] = GetAdditionalPropertyValue(item.AdditionalProperties, "file_path"),
            ["size_bytes"] = GetAdditionalPropertyValue(item.AdditionalProperties, "size_bytes")
        });
}
