using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.JassieAI;

public partial class JassieAIProvider
{
    private sealed class JassieNativeRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<JassieNativeMessage> Messages { get; set; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("maxTokens")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MaxTokens { get; set; }

        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? Temperature { get; set; }

        [JsonPropertyName("image")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Image { get; set; }

        [JsonPropertyName("video")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Video { get; set; }

        [JsonPropertyName("web")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Web { get; set; }
    }

    private sealed class JassieNativeMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class JassieNativeResponse
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("chunks")]
        public int? Chunks { get; set; }

        [JsonPropertyName("duration_seconds")]
        public double? DurationSeconds { get; set; }

        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("sources")]
        public List<JassieNativeSource>? Sources { get; set; }

        [JsonPropertyName("error")]
        public JsonElement? Error { get; set; }
    }

    private sealed class JassieNativeSource
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
    }

    private sealed class JassieCollectedMedia
    {
        public List<string> Images { get; } = [];
        public List<string> Videos { get; } = [];
    }
}
