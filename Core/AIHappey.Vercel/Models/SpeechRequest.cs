using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>
/// Vercel Model Gateway v3 compatible request DTO for <c>POST /v1/audio/speech</c>.
/// <para>
/// This JSON shape is a public, contract-locked surface. Do not rename properties, change casing,
/// alter types, or restructure this DTO.
/// </para>
/// </summary>
public class SpeechRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

}

/// <summary>
/// Vercel Model Gateway v3 compatible response DTO for <c>POST /v1/audio/speech</c>.
/// </summary>
public class SpeechResponse
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    /// <summary>
    /// Generated audio. Typically a data-url string (<c>data:audio/...;base64,...</c>) but may be provider-dependent.
    /// </summary>
    [JsonPropertyName("audio")]
    public SpeechAudioResponse Audio { get; set; } = null!;

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("response")]
    public ResponseData Response { get; set; } = default!;

}

public class SpeechAudioResponse
{
    [JsonPropertyName("base64")]
    public string Base64 { get; set; } = null!;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = null!;

    [JsonPropertyName("format")]
    public string Format { get; set; } = null!;


}
