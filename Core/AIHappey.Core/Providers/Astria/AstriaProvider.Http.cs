using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astria;

public sealed partial class AstriaProvider
{
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken, bool authenticate = true)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (authenticate)
        {
            var key = _keyResolver.Resolve(GetIdentifier());
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("No Astria API key.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var response = await _client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Astria request '{path}' failed ({(int)response.StatusCode}): {body}",
                null, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, object?> Options(Dictionary<string, JsonElement>? options)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options?.TryGetValue("astria", out var native) == true && native.ValueKind == JsonValueKind.Object)
            foreach (var property in native.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        return result;
    }

    private static MultipartFormDataContent Form(Dictionary<string, object?> options)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, value) in options)
            AddField(form, $"prompt[{name}]", JsonSerializer.SerializeToElement(value));
        return form;
    }

    private static void AddField(MultipartFormDataContent form, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var child in value.EnumerateObject())
                AddField(form, $"{name}[{child.Name}]", child.Value);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray())
                AddField(form, $"{name}[]", child);
        else
            form.Add(new StringContent(value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => value.GetRawText()
            }), name);
    }

    private static void AddMedia(MultipartFormDataContent form, string field, string? url, string? data, string? mediaType)
    {
        url ??= RemoteMediaUrl(data);
        if (url is not null)
        {
            form.Add(new StringContent(url), $"prompt[{field}_url]");
            return;
        }
        AddBinary(form, $"prompt[{field}]", data, mediaType);
    }

    private static string? RemoteMediaUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? value : null;

    private static void RemoveMediaOptions(Dictionary<string, object?> options, string field)
    {
        options.Remove(field);
        options.Remove(field + "_url");
        options.Remove(field + "_base64");
    }

    private static void AddBinary(MultipartFormDataContent form, string field, string? data, string? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Astria media must be base64 encoded.");
            mediaType ??= data[5..data.IndexOf(';')];
            data = data[(comma + 1)..];
        }
        var content = new ByteArrayContent(Convert.FromBase64String(data));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType ?? "image/png");
        form.Add(content, field, "input");
    }

    private async Task<JsonElement> SubmitAsync(string tune, MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Post, $"tunes/{tune}/prompts", form, cancellationToken);
        if (result.ValueKind == JsonValueKind.Array)
        {
            if (result.GetArrayLength() != 1)
                throw new InvalidOperationException("Astria returned an unexpected number of prompts.");
            result = result[0].Clone();
        }
        if (result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Astria returned an invalid prompt.");
        _ = PromptId(result);
        return result;
    }

    private Task<JsonElement> PollAsync(string tune, string id, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"tunes/{tune}/prompts/{id}", null, cancellationToken);

    private static string PromptId(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id)
            && PositiveId(id.ValueKind == JsonValueKind.String ? id.GetString() : id.ToString()) is { } value
                ? value : throw new InvalidOperationException("Astria prompt response did not include a valid id.");

    private static string? Text(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Outputs(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
                ? images.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)) : [];

    private static bool Terminal(JsonElement root)
        => !string.IsNullOrWhiteSpace(Text(root, "user_error")) || Outputs(root).Any();

    private Dictionary<string, JsonElement> Metadata(JsonElement root)
    {
        decimal? cost = root.TryGetProperty("cost_mc", out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number)
            ? number / 100000m : null;
        return GetIdentifier().CreatePrimitiveProviderMetadata(root, cost);
    }

    private async Task<(string Data, string MediaType)> DownloadAsync(string url, string fallback, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Astria returned an invalid media URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (Convert.ToBase64String(bytes), response.Content.Headers.ContentType?.MediaType ?? fallback);
    }
}
