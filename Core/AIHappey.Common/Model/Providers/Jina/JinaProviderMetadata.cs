using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Jina;

public class JinaProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    [JsonPropertyName("no_direct_answer")]
    public bool? NoDirectAnswer { get; set; }

    [JsonPropertyName("search_provider")]
    public string? SearchProvider { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("team_size")]
    public int? TeamSize { get; set; }

    [JsonPropertyName("bad_hostnames")]
    public IEnumerable<string>? BadHostnames { get; set; }

    [JsonPropertyName("boost_hostnames")]
    public IEnumerable<string>? BoostHostnames { get; set; }

    [JsonPropertyName("only_hostnames")]
    public IEnumerable<string>? OnlyHostnames { get; set; }

    [JsonPropertyName("max_returned_urls")]
    public string? MaxReturnedUrls { get; set; }
}

