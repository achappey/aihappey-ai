namespace AIHappey.Common.Model.Providers.UVoiceAI;

/// <summary>
/// Optional UVoiceAI speech settings mapped to the API <c>settings</c> payload.
/// </summary>
public sealed class UVoiceAISpeechProviderMetadata
{
    /// <summary>
    /// Optional output type: <c>binary</c> (default) or <c>url</c>.
    /// </summary>
    public string? OutputType { get; set; }

    /// <summary>
    /// Optional output format: <c>wav</c> or <c>mp3</c>.
    /// </summary>
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Optional Thai auto-break handling.
    /// </summary>
    public bool? AutoBreak { get; set; }

    /// <summary>
    /// Optional volume (0.5 - 1.5).
    /// </summary>
    public float? Volume { get; set; }

    /// <summary>
    /// Optional pitch (0.5 - 1.5).
    /// </summary>
    public float? Pitch { get; set; }

    /// <summary>
    /// Optional tone key (-6 to 6).
    /// </summary>
    public int? Key { get; set; }
}

