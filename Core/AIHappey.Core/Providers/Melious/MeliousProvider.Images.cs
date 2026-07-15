using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Melious;

public partial class MeliousProvider
{
  private static readonly JsonSerializerOptions MeliousImageJsonOptions = new(JsonSerializerDefaults.Web)
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
  };

  public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
  {
    ApplyAuthHeader();

    ArgumentNullException.ThrowIfNull(request);

    if (string.IsNullOrWhiteSpace(request.Model))
      throw new ArgumentException("Model is required.", nameof(request));

    if (string.IsNullOrWhiteSpace(request.Prompt))
      throw new ArgumentException("Prompt is required.", nameof(request));

    var now = DateTime.UtcNow;
    var warnings = new List<object>();
    var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
    var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

    MergeMeliousProviderOptions(payload, providerOptions);

    payload["model"] = request.Model.Trim();
    payload["prompt"] = request.Prompt;
    payload["response_format"] = "b64_json";

    if (request.N is not null)
      payload["n"] = request.N.Value;

    if (!string.IsNullOrWhiteSpace(request.Size))
      payload["size"] = request.Size.Trim();

    if (request.Seed is not null)
      payload["seed"] = request.Seed.Value;

    var files = request.Files?.Where(file => file is not null).ToList() ?? [];
    if (files.Count > 0)
    {
      payload["input_images"] = files
          .Select(static file => file.Data.RemoveDataUrlPrefix())
          .ToArray();
    }

    if (request.Mask is not null)
    {
      warnings.Add(new
      {
        type = "unsupported",
        feature = "mask",
        details = "Melious image generation supports model-specific input_images, but no generic mask field is documented. Pass mask-like model parameters through providerOptions.melious when supported by the selected model."
      });
    }

    var jsonBody = JsonSerializer.Serialize(payload, MeliousImageJsonOptions);
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
    {
      Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
    };

    using var response = await _client.SendAsync(httpRequest, cancellationToken);
    var raw = await response.Content.ReadAsStringAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException($"Melious image generation failed ({(int)response.StatusCode}): {raw}");

    using var doc = JsonDocument.Parse(raw);
    var root = doc.RootElement;

    if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
      throw new InvalidOperationException("Melious did not return image data.");

    var imageMimeType = InferMeliousImageMimeType(payload);
    var images = new List<string>();
    var revisedPrompts = new List<string>();

    foreach (var item in dataEl.EnumerateArray())
    {
      var b64 = ReadMeliousStringProperty(item, "b64_json");
      if (!string.IsNullOrWhiteSpace(b64))
        images.Add(b64!.ToDataUrl(imageMimeType));

      var revisedPrompt = ReadMeliousStringProperty(item, "revised_prompt");
      if (!string.IsNullOrWhiteSpace(revisedPrompt))
        revisedPrompts.Add(revisedPrompt!);
    }

    if (images.Count == 0)
      throw new InvalidOperationException("Melious did not return any usable images.");

    return new ImageResponse
    {
      Images = images,
      Warnings = warnings,
      ProviderMetadata = new Dictionary<string, JsonElement>
      {
        [GetIdentifier()] = JsonSerializer.SerializeToElement(new
        {
          energy_cost = ReadMeliousDecimalProperty(root, "energy_cost"),
          credits_cost = ReadMeliousDecimalProperty(root, "credits_cost")
        }, JsonSerializerOptions.Web)
      },
      Response = new()
      {
        Timestamp = now,
        Headers = response.GetHeaders(),
        ModelId = request.Model.ToModelId(GetIdentifier())
      }
    };
  }

  private static string InferMeliousImageMimeType(IReadOnlyDictionary<string, object?> payload)
  {
    if (!payload.TryGetValue("output_format", out var outputFormat) || outputFormat is null)
      return MediaTypeNames.Image.Png;

    var format = outputFormat switch
    {
      JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
      _ => outputFormat.ToString()
    };

    return format?.Trim().ToLowerInvariant() switch
    {
      "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
      "webp" => "image/webp",
      "gif" => MediaTypeNames.Image.Gif,
      _ => MediaTypeNames.Image.Png
    };
  }
}
