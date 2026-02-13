using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Synexa;

public sealed class SynexaProviderMetadata
{
    [JsonPropertyName("wait")]
    public SynexaWaitOptions? Wait { get; set; }
}

public sealed class SynexaImageProviderMetadata
{
    [JsonPropertyName("wait")]
    public SynexaWaitOptions? Wait { get; set; }
}

public sealed class SynexaVideoProviderMetadata
{
    [JsonPropertyName("wait")]
    public SynexaWaitOptions? Wait { get; set; }
}

public sealed class SynexaTranscriptionProviderMetadata
{
    [JsonPropertyName("wait")]
    public SynexaWaitOptions? Wait { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("translate")]
    public bool? Translate { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}

public sealed class SynexaWaitOptions
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("intervalMs")]
    public int? IntervalMs { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int? TimeoutSeconds { get; set; }
}

