using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>The model is supplied by the route, not by the AI SDK request body.</summary>
public sealed class RealtimeTokenRequest
{
    [JsonPropertyName("sessionConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealtimeSessionConfig? SessionConfig { get; set; }
}

public sealed class RealtimeSessionRequest
{
    [Required]
    [JsonPropertyName("sdp")]
    public required string Sdp { get; set; }

    [JsonPropertyName("sessionConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealtimeSessionConfig? SessionConfig { get; set; }
}

public sealed class RealtimeTokenResponse
{
    [Required]
    [JsonPropertyName("token")]
    public required string Token { get; set; }

    [Required]
    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RealtimeToolDefinition>? Tools { get; set; }
}

public sealed class RealtimeSessionResponse
{
    [Required]
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; set; }

    [Required]
    [JsonPropertyName("sdp")]
    public required string Sdp { get; set; }
}

/// <summary>Provider-neutral RealtimeModelV4SessionConfig; no provider translation is performed here.</summary>
[JsonConverter(typeof(RealtimeSessionConfigJsonConverter))]
public class RealtimeSessionConfig
{
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("outputModalities")]
    public List<string>? OutputModalities { get; set; }

    [JsonPropertyName("inputAudioFormat")]
    public RealtimeAudioFormat? InputAudioFormat { get; set; }

    [JsonPropertyName("outputAudioFormat")]
    public RealtimeAudioFormat? OutputAudioFormat { get; set; }

    [JsonPropertyName("inputAudioTranscription")]
    public RealtimeAudioTranscription? InputAudioTranscription { get; set; }

    [JsonPropertyName("outputAudioTranscription")]
    public RealtimeAudioTranscription? OutputAudioTranscription { get; set; }

    private RealtimeTurnDetection? turnDetection;

    [JsonPropertyName("turnDetection")]
    public RealtimeTurnDetection? TurnDetection
    {
        get => turnDetection;
        set { turnDetection = value; HasTurnDetection = true; }
    }

    [JsonIgnore]
    public bool HasTurnDetection { get; private set; }

    [JsonPropertyName("tools")]
    public List<RealtimeToolDefinition>? Tools { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
}

public sealed class RealtimeAudioFormat
{
    [Required]
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("rate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Rate { get; set; }
}

public sealed class RealtimeAudioTranscription
{
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }
}

public sealed class RealtimeTurnDetection
{
    [Required]
    [AllowedValues("server-vad", "semantic-vad", "disabled")]
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Threshold { get; set; }

    [JsonPropertyName("silenceDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SilenceDurationMs { get; set; }

    [JsonPropertyName("prefixPaddingMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PrefixPaddingMs { get; set; }
}

public sealed class RealtimeToolDefinition
{
    [JsonPropertyName("type")]
    [AllowedValues("function")]
    public string Type { get; set; } = "function";

    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [Required]
    [JsonPropertyName("parameters")]
    public required Dictionary<string, JsonElement> Parameters { get; set; }
}

/// <summary>Preserves omitted VAD configuration versus explicit null (push-to-talk).</summary>
public sealed class RealtimeSessionConfigJsonConverter : JsonConverter<RealtimeSessionConfig>
{
    // An undecorated derived type uses the ordinary property deserializer, including
    // the TurnDetection setter only when the incoming property is present.
    private sealed class WireConfig : RealtimeSessionConfig;

    public override RealtimeSessionConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<WireConfig>(ref reader, options);

    public override void Write(Utf8JsonWriter writer, RealtimeSessionConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        Write("instructions", value.Instructions);
        Write("voice", value.Voice);
        Write("outputModalities", value.OutputModalities);
        Write("inputAudioFormat", value.InputAudioFormat);
        Write("outputAudioFormat", value.OutputAudioFormat);
        Write("inputAudioTranscription", value.InputAudioTranscription);
        Write("outputAudioTranscription", value.OutputAudioTranscription);
        if (value.HasTurnDetection)
        {
            writer.WritePropertyName("turnDetection");
            JsonSerializer.Serialize(writer, value.TurnDetection, options);
        }
        Write("tools", value.Tools);
        Write("providerOptions", value.ProviderOptions);
        writer.WriteEndObject();

        void Write<T>(string name, T? item)
        {
            if (item is null) return;
            writer.WritePropertyName(name);
            JsonSerializer.Serialize(writer, item, options);
        }
    }
}
