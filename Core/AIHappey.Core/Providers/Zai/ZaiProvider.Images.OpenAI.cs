using System.Runtime.CompilerServices;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider
{

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        return _client.OpenAICompatibleImageGenerationRequestAsync(
            options,
            "v4/images/generations",
            cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAIImageGenerationRequestAsync(options, cancellationToken);

        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            var base64 = image.B64Json;
            if (string.IsNullOrWhiteSpace(base64) && !string.IsNullOrWhiteSpace(image.Url))
            {
                using var downloadResponse = await _downloadClient.GetAsync(image.Url, cancellationToken);
                var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);

                if (!downloadResponse.IsSuccessStatusCode || bytes.Length == 0)
                    throw new InvalidOperationException(
                        $"Z.AI image download failed ({(int)downloadResponse.StatusCode} {downloadResponse.ReasonPhrase}).");

                base64 = Convert.ToBase64String(bytes);
            }

            if (string.IsNullOrWhiteSpace(base64))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = base64,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
