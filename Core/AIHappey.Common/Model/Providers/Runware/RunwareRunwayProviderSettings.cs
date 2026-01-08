using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware Runway providerSettings.
/// Used for Runware-proxied Runway models (e.g. <c>runway:4@1</c>, <c>runway:4@2</c>).
/// </summary>
public sealed class RunwareRunwayProviderSettings
{
    [JsonPropertyName("contentModeration")]
    public RunwareRunwayContentModeration? ContentModeration { get; set; }
}

