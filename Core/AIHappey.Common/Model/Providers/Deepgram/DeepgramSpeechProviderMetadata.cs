using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Deepgram;

/// <summary>
/// Provider-specific options for Deepgram Text-to-Speech (POST /v1/speak).
/// These values map directly to Deepgram query parameters.
/// </summary>
public sealed class DeepgramSpeechProviderMetadata
{
    /// <summary>
    /// Output encoding (query param: <c>encoding</c>) e.g. mp3, linear16, opus, aac, flac, mulaw, alaw.
    /// </summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    /// <summary>
    /// Output container/wrapper (query param: <c>container</c>) e.g. wav (for linear16/mulaw/alaw), ogg (for opus).
    /// </summary>
    [JsonPropertyName("container")]
    public string? Container { get; set; }

    /// <summary>
    /// Sample rate in Hz (query param: <c>sample_rate</c>).
    /// </summary>
    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    /// <summary>
    /// Bitrate in bits per second (query param: <c>bit_rate</c>).
    /// </summary>
    [JsonPropertyName("bit_rate")]
    public int? BitRate { get; set; }

    /// <summary>
    /// Opt out from Deepgram MIP (query param: <c>mip_opt_out</c>).
    /// </summary>
    [JsonPropertyName("mip_opt_out")]
    public bool? MipOptOut { get; set; }

    /// <summary>
    /// Usage reporting tag (query param: <c>tag</c>). Deepgram accepts string or list-of-strings.
    /// </summary>
    [JsonPropertyName("tag")]
    public JsonElement? Tag { get; set; }

    /// <summary>
    /// Optional callback URL (query param: <c>callback</c>).
    /// </summary>
    [JsonPropertyName("callback")]
    public string? Callback { get; set; }

    /// <summary>
    /// Optional callback HTTP method (query param: <c>callback_method</c>) e.g. POST or PUT.
    /// </summary>
    [JsonPropertyName("callback_method")]
    public string? CallbackMethod { get; set; }
}

