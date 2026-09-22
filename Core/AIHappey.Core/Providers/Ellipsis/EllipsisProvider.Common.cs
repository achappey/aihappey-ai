using System.Net.Http.Headers;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Ellipsis;

public partial class EllipsisProvider
{
    private const string EllipsisSessionsEndpoint = "v1/sessions";
    private const string EllipsisSessionToolName = "create_ellipsis_session";
    private static readonly JsonSerializerOptions EllipsisJson = JsonSerializerOptions.Web;

    private async Task<JsonElement> SendEllipsisJsonAsync(
        HttpMethod method,
        string uri,
        object? payload = null,
        string operation = "Ellipsis request",
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var httpRequest = new HttpRequestMessage(method, uri);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (payload is not null)
        {
            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(payload, EllipsisJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} failed with status {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { }, EllipsisJson);

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body, EllipsisJson).Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"{operation} returned invalid JSON.", exception);
        }
    }

    private async Task<JsonElement?> TryGetEllipsisJsonAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendEllipsisJsonAsync(
                HttpMethod.Get,
                uri,
                operation: $"Ellipsis GET {uri}",
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetEllipsisProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Clone();
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetEllipsisString(JsonElement element, string name)
    {
        if (!TryGetEllipsisProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static bool? GetEllipsisBoolean(JsonElement element, string name)
    {
        if (!TryGetEllipsisProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static long? GetEllipsisInt64(JsonElement element, string name)
    {
        if (!TryGetEllipsisProperty(element, name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset? GetEllipsisDateTimeOffset(JsonElement element, string name)
        => DateTimeOffset.TryParse(GetEllipsisString(element, name), out var parsed) ? parsed : null;

    private static string? GetEllipsisNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (!TryGetEllipsisProperty(current, name, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool? GetEllipsisNestedBoolean(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (!TryGetEllipsisProperty(current, name, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private T? GetEllipsisOption<T>(AIRequest request, params string[] names)
    {
        if (request.Metadata is null)
            return default;

        foreach (var name in names)
        {
            try
            {
                var value = request.Metadata.GetProviderOption<T>(GetIdentifier(), name);
                if (value is not null)
                    return value;
            }
            catch
            {
                // Provider metadata may be supplied as dictionaries, anonymous objects, or JsonElement.
            }
        }

        return default;
    }

    private static string NormalizeEllipsisModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Ellipsis requires a model.", nameof(model));

        var normalized = model.Trim().Trim('/');
        return normalized.StartsWith("ellipsis/", StringComparison.OrdinalIgnoreCase)
            ? normalized["ellipsis/".Length..]
            : normalized;
    }

    private static string ExtractLatestEllipsisUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = string.Join(
                "\n",
                item.Content?.OfType<AITextContentPart>()
                    .Select(static part => part.Text)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                ?? []);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text;
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            return request.Instructions;

        throw new InvalidOperationException("Ellipsis requires a non-empty user message.");
    }

    private static List<object> ExtractEllipsisImages(AIRequest request)
    {
        var images = new List<object>();
        foreach (var file in request.Input?.Items?
                     .Where(static item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(static item => item.Content ?? [])
                     .OfType<AIFileContentPart>() ?? [])
        {
            if (file.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                continue;

            var data = file.Data?.ToString();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            var comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                data = data[(comma + 1)..];

            images.Add(new Dictionary<string, object?>
            {
                ["media_type"] = file.MediaType,
                ["data"] = data
            });
        }

        return images;
    }

    private static object? ExtractEllipsisOutputSchema(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var element = responseFormat is JsonElement json
            ? json
            : JsonSerializer.SerializeToElement(responseFormat, EllipsisJson);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetEllipsisProperty(element, "schema", out var schema))
            return schema;
        if (TryGetEllipsisProperty(element, "json_schema", out var jsonSchema))
        {
            if (TryGetEllipsisProperty(jsonSchema, "schema", out schema))
                return schema;
            return jsonSchema;
        }

        return element;
    }

    private static string CreateEllipsisIdempotencyKey(AIRequest request, string sessionId, string message)
    {
        var seed = $"{request.Id}\n{sessionId}\n{message}";
        return "aihappey-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..32];
    }
}
