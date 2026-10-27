using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI.Live;

public sealed class LiveSessionRequest
{
    [Required]
    [JsonPropertyName("session")]
    public required LiveSessionConfig Session { get; set; }

    [Required]
    [JsonPropertyName("transport")]
    public required LiveRequestTransport Transport { get; set; }
}

public sealed class LiveSessionResponse
{
    [Required]
    [JsonPropertyName("session")]
    public required LiveSessionReference Session { get; set; }

    [Required]
    [JsonPropertyName("transport")]
    public required LiveResponseTransport Transport { get; set; }
}

public sealed class LiveSessionReference
{
    [Required]
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}

public sealed class LiveSessionConfig
{
    [Required]
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveAudioConfig? Audio { get; set; }

    [JsonPropertyName("client")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveClientConfig? Client { get; set; }

    [JsonPropertyName("delegation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveDelegation? Delegation { get; set; }

    [MaxLength(128)]
    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LiveInitialItem>? Input { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; set; }

    [JsonPropertyName("triggers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LiveDecisionTrigger>? Triggers { get; set; }
}

public sealed class LiveAudioConfig
{
    [JsonPropertyName("output")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveAudioOutputConfig? Output { get; set; }
}

public sealed class LiveAudioOutputConfig
{
    [JsonPropertyName("voice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveVoice? Voice { get; set; }
}

/// <summary>A built-in voice name (including future names) or a custom voice ID object.</summary>
[JsonConverter(typeof(LiveVoiceJsonConverter))]
public sealed class LiveVoice
{
    public string? Name { get; }
    public LiveCustomVoice? Custom { get; }

    public LiveVoice(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));
    public LiveVoice(LiveCustomVoice custom) => Custom = custom ?? throw new ArgumentNullException(nameof(custom));
    public static implicit operator LiveVoice(string name) => new(name);
}

public sealed class LiveCustomVoice
{
    [Required]
    [JsonPropertyName("id")]
    public required string Id { get; set; }
}

public sealed class LiveClientConfig
{
    [Required]
    [JsonPropertyName("data_channel")]
    public required LiveDataChannelConfig DataChannel { get; set; }
}

public sealed class LiveDataChannelConfig
{
    [JsonPropertyName("allowed_client_events")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveEventPermissions<string>? AllowedClientEvents { get; set; }

    [JsonPropertyName("allowed_server_events")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveEventPermissions<LiveServerEventSelector>? AllowedServerEvents { get; set; }
}

/// <summary>Exactly "all" or an array; an empty array means no events, not all events.</summary>
[JsonConverter(typeof(LiveEventPermissionsJsonConverterFactory))]
public sealed class LiveEventPermissions<T>
{
    public bool IsAll { get; }
    public IReadOnlyList<T>? Events { get; }

    private LiveEventPermissions() => IsAll = true;
    public LiveEventPermissions(IReadOnlyList<T> events) => Events = events ?? throw new ArgumentNullException(nameof(events));
    public static LiveEventPermissions<T> All() => new();
}

public sealed class LiveServerEventSelector : IValidatableObject
{
    [Required]
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("response_event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseEvent { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type == "response.event" ? string.IsNullOrWhiteSpace(ResponseEvent) : ResponseEvent is not null)
            yield return new ValidationResult("response_event is required only for response.event selectors.", [nameof(ResponseEvent)]);
    }
}

[JsonConverter(typeof(LiveRequestTransportJsonConverter))]
public abstract class LiveRequestTransport(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveWebRtcRequestTransport() : LiveRequestTransport("webrtc")
{
    [Required]
    [JsonPropertyName("sdp")]
    public required string Sdp { get; set; }
}

public sealed class LiveSipRequestTransport() : LiveRequestTransport("sip")
{
    [Required, RegularExpression(@"\+[1-9]\d{1,14}")]
    [JsonPropertyName("destination")]
    public required string Destination { get; set; }

    [Required]
    [JsonPropertyName("trunk")]
    public required LiveSipTrunk Trunk { get; set; }
}

public sealed class LiveSipTrunk
{
    [Required]
    [JsonPropertyName("auth")]
    public required LiveSipDigestAuth Auth { get; set; }

    [Required, RegularExpression(@"\+[1-9]\d{1,14}")]
    [JsonPropertyName("caller_number")]
    public required string CallerNumber { get; set; }

    /// <summary>TLS sips: endpoint; network destination checks belong to a future SIP implementation.</summary>
    [Required]
    [JsonPropertyName("provider_url")]
    public required string ProviderUrl { get; set; }
}

public sealed class LiveSipDigestAuth : IValidatableObject
{
    [JsonPropertyName("type")]
    [AllowedValues("digest")]
    public string Type { get; set; } = "digest";

    [Required]
    [JsonPropertyName("username")]
    public required string Username { get; set; }

    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("password")]
    public required string Password { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Username is not null && (System.Text.Encoding.UTF8.GetByteCount(Username) > 256 || Username.IndexOfAny(['\r', '\n', '\0']) >= 0))
            yield return new ValidationResult("Invalid SIP username.", [nameof(Username)]);
        if (Password is not null && (System.Text.Encoding.UTF8.GetByteCount(Password) > 4096 || Password.IndexOfAny(['\r', '\n', '\0']) >= 0))
            yield return new ValidationResult("Invalid SIP password.", [nameof(Password)]);
    }
}

// Response transports deliberately cannot carry destination or trunk credentials.
[JsonConverter(typeof(LiveResponseTransportJsonConverter))]
public abstract class LiveResponseTransport(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveWebRtcResponseTransport() : LiveResponseTransport("webrtc")
{
    [Required]
    [JsonPropertyName("sdp")]
    public required string Sdp { get; set; }
}

public sealed class LiveSipResponseTransport() : LiveResponseTransport("sip");

public sealed class LiveInitialItem : IValidatableObject
{
    [Required]
    [AllowedValues("developer", "user", "assistant")]
    [JsonPropertyName("role")]
    public required string Role { get; set; }

    [Required, MinLength(1), MaxLength(1)]
    [JsonPropertyName("content")]
    public required List<LiveInitialText> Content { get; set; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    [LiveAllowedValues("incomplete", "completed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    [LiveAllowedValues("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Content is null) yield break;
        foreach (var part in Content)
            if (part is not null && part.Type is not null && !(Role == "assistant"
                ? part.Type is "text" or "output_text" : part.Type == "input_text"))
                yield return new ValidationResult("History content type does not match its role.", [nameof(Content)]);
    }
}

public sealed class LiveInitialText
{
    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("text")]
    public required string Text { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }
}

/// <summary>Optional string enum validation that accepts omission and explicit null.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LiveAllowedValuesAttribute(params string[] values) : ValidationAttribute
{
    public override bool IsValid(object? value) => value is null || value is string text && values.Contains(text, StringComparer.Ordinal);
}
