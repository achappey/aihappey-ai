using System.Text.Json;

namespace AIHappey.Common.Model;

public class ImageRequest
{

    public string Model { get; set; } = null!;

    public string Prompt { get; set; } = null!;

    public string? Size { get; set; }

    public string? AspectRatio { get; set; }

    public int? Seed { get; set; }

    public int? N { get; set; }

    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    public IEnumerable<ImageFile>? Files { get; set; }

    public ImageFile? Mask { get; set; }

}


public class ImageFile
{

    public string Type { get; set; } = "file";

    public string MediaType { get; set; } = null!;

    public string Data { get; set; } = null!;
}


public class ImageFileUrl
{

    public string Type { get; set; } = "url";

    public string Url { get; set; } = null!;
}


public class ImageResponse
{
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    public IEnumerable<string>? Images { get; set; }

    public IEnumerable<object> Warnings { get; set; } = [];

    public ImageResponseData Response { get; set; } = default!;

}

public class ImageResponseData
{
    public string ModelId { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public object? Body { get; set; }



}