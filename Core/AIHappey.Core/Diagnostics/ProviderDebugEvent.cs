using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Diagnostics;

/// <summary>A presentation-independent, request-scoped provider diagnostic.</summary>
public sealed record ProviderDebugEvent(
    string RequestId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Provider,
    string Operation,
    string OperationId,
    string Kind,
    ProviderDebugPayload Payload);

/// <summary>Original readable text plus native structured data where available.</summary>
public sealed record ProviderDebugPayload(string Content, string Encoding, string MediaType)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EventId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Retry { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Complete { get; init; }

    public static ProviderDebugPayload FromText(string content, string mediaType)
        => new(content, "text", mediaType) { Data = ParseData(content) };

    internal static JsonElement ParseData(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(text);
        }
    }
}

public interface IProviderDebugEmitter
{
    bool Enabled { get; }

    ValueTask EmitAsync(string provider, string operation, string operationId, string kind,
        ProviderDebugPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>Selected by the hosting application, never by a provider.</summary>
public interface IProviderDebugSink
{
    ValueTask WriteAsync(ProviderDebugEvent debugEvent, CancellationToken cancellationToken);
}

public sealed class NullProviderDebugEmitter : IProviderDebugEmitter
{
    public static NullProviderDebugEmitter Instance { get; } = new();
    public bool Enabled => false;
    public ValueTask EmitAsync(string provider, string operation, string operationId, string kind,
        ProviderDebugPayload payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
