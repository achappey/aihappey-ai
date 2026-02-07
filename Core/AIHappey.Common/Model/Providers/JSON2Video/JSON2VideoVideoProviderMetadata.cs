using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.JSON2Video;

/// <summary>
/// ProviderOptions schema for JSON2Video video generation.
/// Consumed via <c>providerOptions.json2video</c> for the unified video flow.
/// </summary>
public sealed class JSON2VideoVideoProviderMetadata
{
    /// <summary>
    /// Raw JSON2Video movie JSON payload. When provided, it is sent as-is to the API.
    /// </summary>
    [JsonPropertyName("movie")]
    public string? Movie { get; set; }
}

