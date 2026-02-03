using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

public sealed class RunwareSafetyOptions
{
    [JsonPropertyName("checkContent")]
    public bool? CheckContent { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

