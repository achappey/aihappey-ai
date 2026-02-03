using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

// Reserved for future video-only metadata extensions. Currently unused.

public sealed class RunwareVideoFrameImage
{
    [JsonPropertyName("inputImages")]
    public string? InputImages { get; set; }

    [JsonPropertyName("frame")]
    public JsonElement? Frame { get; set; }
}

public sealed class RunwareVideoSpeech
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
