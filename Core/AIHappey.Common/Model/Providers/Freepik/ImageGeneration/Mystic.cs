using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class Mystic
{
    [JsonPropertyName("adherence")]
    public int? Adherence { get; set; }

    [JsonPropertyName("hdr")]
    public int? Hdr { get; set; }

    [JsonPropertyName("creative_detailing")]
    public int? CreativeDetailing { get; set; }

    [JsonPropertyName("engine")]
    public string? Engine { get; set; } // automatic, magnific_illusio, magnific_sharpy, magnific_sparkle 

    [JsonPropertyName("fixed_generation")]
    public bool? FixedGeneration { get; set; }

    [JsonPropertyName("filter_nsfw")]
    public bool? FilterNsfw { get; set; }

}


