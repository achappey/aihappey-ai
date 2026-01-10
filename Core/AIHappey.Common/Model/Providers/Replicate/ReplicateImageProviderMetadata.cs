using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Replicate;

public sealed class ReplicateImageProviderMetadata
{
    /// <summary>
    /// Enables Replicate sync mode by setting <c>Prefer: wait=&lt;n&gt;</c>.
    /// Defaults to 60 seconds.
    /// </summary>
    [JsonPropertyName("preferWaitSeconds")]
    public int? PreferWaitSeconds { get; set; }

    /// <summary>
    /// Prediction deadline (cancels the prediction server-side).
    /// Examples: <c>60s</c>, <c>5m</c>, <c>1h</c>.
    /// </summary>
    [JsonPropertyName("cancelAfter")]
    public string? CancelAfter { get; set; }

    /// <summary>
    /// Raw input pass-through to Replicate model input.
    /// Values are merged after built-in fields (prompt/image/width/height/seed).
    /// </summary>
    [JsonPropertyName("inputOverrides")]
    public Dictionary<string, JsonElement>? InputOverrides { get; set; }
}

