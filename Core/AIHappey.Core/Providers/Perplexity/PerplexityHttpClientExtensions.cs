using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Providers.Perplexity.Models;

namespace AIHappey.Core.Providers.Perplexity;

public static class PerplexityHttpClientExtensions
{
    private static readonly JsonSerializerOptions options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<PerplexityChatResponse?> ChatCompletion(this HttpClient httpClient,
         PerplexityChatRequest request, CancellationToken cancellationToken = default)
    {
        // Ensure streaming is off for non-streaming scenario
        request.Stream = false;

        // Serialize the request
        var requestJson = JsonSerializer.Serialize(request, options);

        // Build the HTTP request
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // Send the request
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(responseContent ?? response.ReasonPhrase);
        }

        return JsonSerializer.Deserialize<PerplexityChatResponse>(responseContent, options);
    }

    /// <summary>
    /// Streaming chat completion request to Perplexity.
    /// This method returns partial content as it's received.
    /// </summary>
    public static async IAsyncEnumerable<PerplexityChatResponse> ChatCompletionStreaming(
        this HttpClient httpClient,
        PerplexityChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Enable streaming on the request
        request.Stream = true;

        // Serialize the request
        var requestJson = JsonSerializer.Serialize(request, options);

        // Build the HTTP request
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "completions")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        //    response.EnsureSuccessStatusCode();

        // The exact streaming format from Perplexity might vary, but let's assume line-delimited JSON or SSE-like format.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            // Read data line-by-line or chunk-by-chunk
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var content = line?.StartsWith("data: ") == true ? line["data: ".Length..] : line;
            // Return the raw chunk. The caller can parse partial JSON or text deltas as needed.
            yield return JsonSerializer.Deserialize<PerplexityChatResponse>(content!)!;
        }
    }
}
