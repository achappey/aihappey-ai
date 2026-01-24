using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>
/// Vercel Model Gateway v3 compatible request DTO for <c>POST /v1/audio/transcriptions</c>.
/// <para>
/// This JSON shape is a public, contract-locked surface. Do not rename properties, change casing,
/// alter types, or restructure this DTO.
/// </para>
/// </summary>
public class TranscriptionRequest
{

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    /// <summary>
    /// Audio input as a data-url (<c>data:audio/...;base64,...</c>) or raw base64.
    /// </summary>
    [JsonPropertyName("audio")]
    public object Audio { get; set; } = null!;

    /// <summary>
    /// Media type for <see cref="Audio"/> (e.g. <c>audio/mpeg</c>). Must match the provided audio payload.
    /// </summary>
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = null!;

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

}

/// <summary>
/// Vercel Model Gateway v3 compatible response DTO for <c>POST /v1/audio/transcriptions</c>.
/// </summary>
public class TranscriptionResponse
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("durationInSeconds")]
    public float? DurationInSeconds { get; set; }

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("segments")]
    public IEnumerable<TranscriptionSegment> Segments { get; set; } = [];

    [JsonPropertyName("response")]
    public ResponseData Response { get; set; } = default!;

}


public class TranscriptionSegment
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("startSecond")]
    public float StartSecond { get; set; }

    [JsonPropertyName("endSecond")]
    public float EndSecond { get; set; }


}
