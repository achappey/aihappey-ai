using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;

namespace AIHappey.Core.AI;

public static class ModelProviderImageCompatibilityExtensions
{
    private static readonly JsonSerializerOptions OpenAIImageCompatibilityJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async IAsyncEnumerable<IOpenAIImageStreamEvent>
        OpenAICompatibleImageEditNonStreamingAsStreamAsync(
            this HttpClient httpClient,
            OpenAIImageEditRequest options,
            string? endpoint = "v1/images/edits",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await httpClient.OpenAICompatibleImageEditRequestAsync(
            options,
            endpoint,
            cancellationToken);

        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageEditCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }

    public static async IAsyncEnumerable<IOpenAIImageStreamEvent>
     OpenAICompatibleImageGenerationNonStreamingAsStreamAsync(
         this HttpClient httpClient,
         OpenAIImageGenerationRequest options,
         string? endpoint = "v1/images/generations",
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await httpClient.OpenAICompatibleImageGenerationRequestAsync(
            options,
            endpoint,
            cancellationToken);

        foreach (var image in response.Data ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(image.B64Json))
                continue;

            yield return new OpenAIImageGenerationCompleted
            {
                B64Json = image.B64Json,
                CreatedAt = response.Created,
                Size = response.Size ?? options.Size,
                Quality = response.Quality ?? options.Quality,
                Background = response.Background ?? options.Background,
                OutputFormat = response.OutputFormat ?? options.OutputFormat,
                Usage = response.Usage
            };
        }
    }


    public static async Task<OpenAIImagesResponse>
        OpenAICompatibleImageGenerationRequestAsync(
            this HttpClient httpClient,
            OpenAIImageGenerationRequest options,
            string? endpoint = "v1/images/generations",
            CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateJsonContent(options, forceStream: false)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateImageRequestException("Image generation", response, raw);

        return DeserializeImagesResponse(raw, "image generation");
    }

    public static async IAsyncEnumerable<IOpenAIImageStreamEvent>
        OpenAICompatibleImageGenerationStreamingAsync(
            this HttpClient httpClient,
            OpenAIImageGenerationRequest options,
            string? endpoint = "v1/images/generations",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateJsonContent(options, forceStream: true)
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        await foreach (var streamEvent in httpClient.ReadOpenAICompatibleImageStreamAsync(
            request,
            "Streaming image generation",
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public static async Task<OpenAIImagesResponse>
        OpenAICompatibleImageEditRequestAsync(
            this HttpClient httpClient,
            OpenAIImageEditRequest options,
            string? endpoint = "v1/images/edits",
            CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateImageEditContent(options, forceStream: false, cancellationToken)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateImageRequestException("Image edit", response, raw);

        return DeserializeImagesResponse(raw, "image edit");
    }

    public static async IAsyncEnumerable<IOpenAIImageStreamEvent>
        OpenAICompatibleImageEditStreamingAsync(
            this HttpClient httpClient,
            OpenAIImageEditRequest options,
            string? endpoint = "v1/images/edits",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateImageEditContent(options, forceStream: true, cancellationToken)
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        await foreach (var streamEvent in httpClient.ReadOpenAICompatibleImageStreamAsync(
            request,
            "Streaming image edit",
            cancellationToken))
        {
            yield return streamEvent;
        }
    }

    public static async Task<OpenAIImagesResponse>
        OpenAICompatibleImageVariationRequestAsync(
            this HttpClient httpClient,
            OpenAIImageVariationRequest options,
            string? endpoint = "v1/images/variations",
            CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = CreateImageVariationContent(options, cancellationToken)
        };

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw CreateImageRequestException("Image variation", response, raw);

        return DeserializeImagesResponse(raw, "image variation");
    }

    private static async IAsyncEnumerable<IOpenAIImageStreamEvent>
        ReadOpenAICompatibleImageStreamAsync(
            this HttpClient httpClient,
            HttpRequestMessage request,
            string operationName,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateImageRequestException(operationName, response, error);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var streamEvent in ParseStreamEvent(eventName, dataLines))
                    yield return streamEvent;

                eventName = null;
                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line["data:".Length..].Trim());
        }

        foreach (var streamEvent in ParseStreamEvent(eventName, dataLines))
            yield return streamEvent;
    }

    private static IEnumerable<IOpenAIImageStreamEvent> ParseStreamEvent(string? eventName, List<string> dataLines)
    {
        if (dataLines.Count == 0)
            yield break;

        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            yield break;

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        var type = ReadStreamEventType(root, eventName);

        IOpenAIImageStreamEvent? streamEvent = type switch
        {
            "image_generation.partial_image" => JsonSerializer.Deserialize<OpenAIImageGenerationPartialImage>(data, OpenAIImageCompatibilityJsonOptions),
            "image_generation.completed" => JsonSerializer.Deserialize<OpenAIImageGenerationCompleted>(data, OpenAIImageCompatibilityJsonOptions),
            "image_edit.partial_image" => JsonSerializer.Deserialize<OpenAIImageEditPartialImage>(data, OpenAIImageCompatibilityJsonOptions),
            "image_edit.completed" => JsonSerializer.Deserialize<OpenAIImageEditCompleted>(data, OpenAIImageCompatibilityJsonOptions),
            "error" => throw new InvalidOperationException($"Image stream provider returned an error: {data}"),
            _ => null
        };

        if (streamEvent != null)
            yield return streamEvent;
    }

    private static StringContent CreateJsonContent<T>(T options, bool forceStream)
    {
        var json = JsonSerializer.SerializeToNode(options, OpenAIImageCompatibilityJsonOptions)?.AsObject()
                   ?? throw new InvalidOperationException("Could not serialize OpenAI image request.");

        if (forceStream)
            json["stream"] = true;

        return new StringContent(
            json.ToJsonString(OpenAIImageCompatibilityJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);
    }

    private static HttpContent CreateImageEditContent(
        OpenAIImageEditRequest options,
        bool forceStream,
        CancellationToken cancellationToken)
    {
        if (options.ImageFiles?.Length > 0 || options.MaskFile != null)
            return CreateImageEditMultipartContent(options, forceStream, cancellationToken);

        return CreateJsonContent(options, forceStream);
    }

    private static MultipartFormDataContent CreateImageEditMultipartContent(
        OpenAIImageEditRequest options,
        bool forceStream,
        CancellationToken cancellationToken)
    {
        var content = new MultipartFormDataContent();
        AddMultipartString(content, "model", options.Model);
        AddMultipartString(content, "prompt", options.Prompt);
        AddMultipartString(content, "background", options.Background);
        AddMultipartString(content, "input_fidelity", options.InputFidelity);
        AddMultipartString(content, "moderation", options.Moderation);
        AddMultipartString(content, "n", options.N?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddMultipartString(content, "output_compression", options.OutputCompression?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddMultipartString(content, "output_format", options.OutputFormat);
        AddMultipartString(content, "partial_images", options.PartialImages?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddMultipartString(content, "quality", options.Quality);
        AddMultipartString(content, "size", options.Size);
        AddMultipartString(content, "stream", forceStream ? "true" : options.Stream?.ToString().ToLowerInvariant());
        AddMultipartString(content, "user", options.User);

        var imageFiles = options.ImageFiles ?? [];
        for (var i = 0; i < imageFiles.Length; i++)
        {
            var file = imageFiles[i];
            content.Add(CreateFileContent(file, cancellationToken), "image[]", ResolveFileName(file, $"image-{i}.png"));
        }

        if (options.MaskFile != null)
            content.Add(CreateFileContent(options.MaskFile, cancellationToken), "mask", ResolveFileName(options.MaskFile, "mask.png"));

        return content;
    }

    private static HttpContent CreateImageVariationContent(
        OpenAIImageVariationRequest options,
        CancellationToken cancellationToken)
    {
        if (options.ImageFile == null)
            return CreateJsonContent(options, forceStream: false);

        var content = new MultipartFormDataContent();
        AddMultipartString(content, "model", options.Model);
        AddMultipartString(content, "n", options.N?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddMultipartString(content, "response_format", options.ResponseFormat);
        AddMultipartString(content, "size", options.Size);
        AddMultipartString(content, "user", options.User);
        content.Add(CreateFileContent(options.ImageFile, cancellationToken), "image", ResolveFileName(options.ImageFile, "image.png"));
        return content;
    }

    private static ByteArrayContent CreateFileContent(Microsoft.AspNetCore.Http.IFormFile file, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        file.CopyToAsync(memory, cancellationToken).GetAwaiter().GetResult();
        var content = new ByteArrayContent(memory.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.ContentType)
            ? MediaTypeNames.Application.Octet
            : file.ContentType);
        return content;
    }

    private static string ResolveFileName(Microsoft.AspNetCore.Http.IFormFile file, string fallback)
        => string.IsNullOrWhiteSpace(file.FileName) ? fallback : file.FileName;

    private static void AddMultipartString(MultipartFormDataContent content, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            content.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static OpenAIImagesResponse DeserializeImagesResponse(string raw, string operationName)
        => JsonSerializer.Deserialize<OpenAIImagesResponse>(raw, OpenAIImageCompatibilityJsonOptions)
           ?? throw new InvalidOperationException($"OpenAI compatible {operationName} response was empty.");

    private static string? ReadStreamEventType(JsonElement root, string? eventName)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String)
        {
            return typeElement.GetString();
        }

        return eventName;
    }

    private static InvalidOperationException CreateImageRequestException(
        string operationName,
        HttpResponseMessage response,
        string raw)
        => new(string.IsNullOrWhiteSpace(raw)
            ? $"{operationName} request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
            : $"{operationName} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
}
