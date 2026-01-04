using System.Text.Json;

namespace AIHappey.Common.Model;

public class TranscriptionRequest
{

    public string Model { get; set; } = null!;

    public object Audio { get; set; } = null!;

    public string MediaType { get; set; } = null!;

    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

}

public class TranscriptionResponse
{
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    public string Text { get; set; } = null!;

    public string? Language { get; set; }

    public float? DurationInSeconds { get; set; }

    public IEnumerable<object> Warnings { get; set; } = [];

    public IEnumerable<TranscriptionSegment> Segments { get; set; } = null!;

    public ResponseData Response { get; set; } = default!;

}


public class TranscriptionSegment
{
    public string Text { get; set; } = null!;

    public float StartSecond { get; set; }

    public float EndSecond { get; set; }


}