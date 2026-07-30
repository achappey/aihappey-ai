using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using System.Net.Mime;

namespace AIHappey.Core.Providers.SudoRouter;

public partial class SudoRouterProvider
{
    /// <summary>
    /// Copies provider-scoped options into a mutable payload so documented provider-specific
    /// inputs can pass through without expanding the public AIHappey request contracts.
    /// </summary>
    private JsonObject GetSudoRouterProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null
            || !providerOptions.TryGetValue(GetIdentifier(), out var metadata)
            || metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("SudoRouter provider options must be a JSON object.", nameof(providerOptions));

        return JsonNode.Parse(metadata.GetRawText())?.AsObject()
            ?? throw new ArgumentException("SudoRouter provider options must be a JSON object.", nameof(providerOptions));
    }

    private async Task<SudoRouterJsonResult> SendSudoRouterJsonAsync(
        HttpMethod method,
        string endpoint,
        JsonObject? payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(method, endpoint);
        if (payload is not null)
        {
            request.Content = new StringContent(
                payload.ToJsonString(),
                System.Text.Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"SudoRouter request to '{endpoint}' failed ({(int)response.StatusCode})."
                : $"SudoRouter request to '{endpoint}' failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return new SudoRouterJsonResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<SudoRouterBinaryResult> DownloadSudoRouterMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"SudoRouter media download failed ({(int)response.StatusCode}) for '{url}'.");

        return new SudoRouterBinaryResult(
            bytes,
            response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType,
            response.GetHeaders());
    }

    private static string ToSudoRouterDataUrl(byte[] bytes, string mediaType)
        => $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

    private static string NormalizeSudoRouterBase64(string base64)
    {
        var marker = ";base64,";
        var markerIndex = base64.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0 ? base64[(markerIndex + marker.Length)..] : base64;
    }

    private sealed record SudoRouterJsonResult(JsonElement Root, IDictionary<string, string> Headers);

    private sealed record SudoRouterBinaryResult(byte[] Bytes, string MediaType, IDictionary<string, string> Headers);
}
