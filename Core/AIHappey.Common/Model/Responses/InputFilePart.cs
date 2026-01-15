
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;

public sealed class InputFilePart : ResponseContentPart
{
    public InputFilePart()
    {
        Type = "input_file";
    }

    [JsonPropertyName("file_data")]
    public string? FileData { get; set; }

    [JsonPropertyName("file_id")]
    public string? FileId { get; set; }

    [JsonPropertyName("file_url")]
    public string? FileUrl { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }
}
