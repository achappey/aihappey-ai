using AIHappey.Core.AI;
using System.Text.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var payload = imageRequest.GetImageRequestPayload();

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync("v1/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {text}");

        var root = JsonNode.Parse(text) ?? throw new Exception($"Something went wrong");
        var data = root["data"]!.AsArray();
        var images = new List<string>();

        foreach (var item in data)
        {
            byte[] bytes;

            if (item?["b64_json"] is JsonNode b64)
            {
                images.Add(b64.GetValue<string>().ToDataUrl(MediaTypeNames.Image.Png));
                bytes = Convert.FromBase64String(b64.GetValue<string>());
            }
            else if (item?["url"] is JsonNode urlNode)
            {
                var url = urlNode.GetValue<string>();
                bytes = await _client.GetByteArrayAsync(url, cancellationToken);
                images.Add(Convert.ToBase64String(bytes).ToDataUrl(MediaTypeNames.Image.Png));
            }
            else
            {
                continue;
            }
        }

        return new()
        {
            Images = images,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };
    }

}
