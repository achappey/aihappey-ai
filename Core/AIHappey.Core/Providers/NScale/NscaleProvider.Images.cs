using AIHappey.Core.AI;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using System.Text;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Nscale;

public partial class NscaleProvider
{
    private static readonly JsonSerializerOptions imageSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
       OpenAIImageGenerationRequest options,
       CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();

        var response = await ImageRequest(
            options.ToImageRequest(options.Model, GetIdentifier()),
            cancellationToken);

        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var payload = new JsonObject();
        if (imageRequest.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true)
        {
            if (providerOptions.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Provider options for {GetIdentifier()} must be a JSON object.");

            foreach (var property in providerOptions.EnumerateObject())
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        if (imageRequest.Prompt is not null)
            payload["prompt"] = imageRequest.Prompt;
        if (imageRequest.Model is not null)
            payload["model"] = imageRequest.Model;
        if (imageRequest.N is not null)
            payload["n"] = imageRequest.N.Value;
        if (imageRequest.Size is not null)
            payload["size"] = imageRequest.Size;

        var jsonBody = JsonSerializer.Serialize(payload, imageSettings);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "v1/images/generations"
        )
        {
            Content = new StringContent(
                jsonBody,
                Encoding.UTF8,
                "application/json"
            )
        };

        // Step 3: Send request
        using var resp = await _client.SendAsync(request, cancellationToken);
        var jsonResponse = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            try
            {

                using var errorDoc = JsonDocument.Parse(jsonResponse);
                var message = errorDoc.RootElement
                    .GetProperty("error")
                    .GetProperty("message")
                    .GetString();

                throw new Exception(message ?? jsonResponse);
            }
            catch (JsonException)
            {
                // fallback als het geen geldige JSON is
                throw new Exception(jsonResponse);
            }
        }

        List<string> images = [];
        List<object> warnings = [];
        using var doc = JsonDocument.Parse(jsonResponse);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new Exception("No image data array returned from Nscale API.");

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop))
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl("image/png"));
        }

        if (images.Count == 0)
            throw new Exception("No valid image data returned from Nscale API.");

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
            });
        }

        return new()
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model
            }
        };

    }
}