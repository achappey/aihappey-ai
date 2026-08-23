using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Melious;

public partial class MeliousProvider
{
  public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
    OpenAIImageGenerationRequest options,
    CancellationToken cancellationToken = default)
  {
    options.ValidateOpenAIImageGenerationRequest();

    if (string.IsNullOrWhiteSpace(options.Model))
      throw new ArgumentException("'model' is a required field", nameof(options));

    if (options.N is < 1 or > 10)
      throw new ArgumentOutOfRangeException(nameof(options), "Melious image count must be between 1 and 10.");

    if (!string.IsNullOrWhiteSpace(options.ResponseFormat)
      && !string.Equals(options.ResponseFormat, "b64_json", StringComparison.OrdinalIgnoreCase))
    {
      throw new NotSupportedException("Melious image generation supports only response_format 'b64_json'.");
    }

    var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var (name, value) in options.AdditionalProperties ?? [])
      payload[name] = value.Clone();

    payload["model"] = options.Model.Trim();
    payload["prompt"] = options.Prompt;
    payload["response_format"] = "b64_json";

    if (options.N is not null)
      payload["n"] = options.N.Value;
    if (!string.IsNullOrWhiteSpace(options.Size))
      payload["size"] = options.Size.Trim();
    if (!string.IsNullOrWhiteSpace(options.Quality))
      payload["quality"] = options.Quality.Trim();
    if (!string.IsNullOrWhiteSpace(options.Style))
      payload["style"] = options.Style.Trim();
    if (!string.IsNullOrWhiteSpace(options.User))
      payload["user"] = options.User;
    if (!string.IsNullOrWhiteSpace(options.OutputFormat))
      payload["output_format"] = options.OutputFormat.Trim();
    if (options.OutputCompression is not null)
      payload["output_quality"] = options.OutputCompression.Value;

    ApplyAuthHeader();
    using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
    {
      Content = new StringContent(
        JsonSerializer.Serialize(payload, MeliousImageJsonOptions),
        Encoding.UTF8,
        MediaTypeNames.Application.Json)
    };
    using var response = await _client.SendAsync(request, cancellationToken);
    var raw = await response.Content.ReadAsStringAsync(cancellationToken);

    if (!response.IsSuccessStatusCode)
      throw new InvalidOperationException($"Melious image generation failed ({(int)response.StatusCode}): {raw}");

    using var document = JsonDocument.Parse(raw);
    var root = document.RootElement;
    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
      throw new InvalidOperationException("Melious image generation response did not include a data array.");

    var images = data.EnumerateArray()
      .Select(item => new OpenAIImageData
      {
        B64Json = ReadMeliousStringProperty(item, "b64_json"),
        RevisedPrompt = ReadMeliousStringProperty(item, "revised_prompt")
      })
      .Where(static image => !string.IsNullOrWhiteSpace(image.B64Json))
      .ToList();

    if (images.Count == 0)
      throw new InvalidOperationException("Melious image generation returned no usable images.");

    return new OpenAIImagesResponse
    {
      Created = root.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp)
        ? timestamp
        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      Background = string.Equals(options.Background, "auto", StringComparison.OrdinalIgnoreCase)
        ? null
        : options.Background,
      OutputFormat = options.OutputFormat,
      Quality = options.Quality,
      Size = options.Size,
      Data = images
    };
  }

  public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
    OpenAIImageGenerationRequest options,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);
    foreach (var image in response.Data ?? [])
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (string.IsNullOrWhiteSpace(image.B64Json))
        continue;

      yield return new OpenAIImageGenerationCompleted
      {
        B64Json = image.B64Json,
        CreatedAt = response.Created,
        Background = response.Background,
        OutputFormat = response.OutputFormat,
        Quality = response.Quality,
        Size = response.Size,
        Usage = response.Usage
      };
    }
  }

  public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
  {
    throw new NotSupportedException("Melious does not document an OpenAI-compatible image edits endpoint.");
  }

  public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
  {
    throw new NotSupportedException("Melious does not document an OpenAI-compatible image edits endpoint.");
  }


}
