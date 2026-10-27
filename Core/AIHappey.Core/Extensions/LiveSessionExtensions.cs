using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.OpenAI.Live;

namespace AIHappey.Core.Extensions;

/// <summary>Transport for providers that implement the OpenAI Live sessions contract.</summary>
public static class LiveSessionExtensions
{
    internal static readonly JsonSerializerOptions SessionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates one session using the caller's authenticated client and endpoint.
    /// Never retries: a failed response can still have placed a SIP call.
    /// </summary>
    public static async Task<LiveSessionResponse> OpenAICompatibleLiveSessionAsync(
        this HttpClient client,
        LiveSessionRequest request,
        string endpoint = "v1/live/sessions",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateGraph(request);
        if (request.Transport is not (LiveWebRtcRequestTransport or LiveSipRequestTransport))
            throw new ArgumentException("Unsupported Live transport.", nameof(request));

        var payload = JsonSerializer.SerializeToNode(request, SessionJsonOptions)!.AsObject();
        var root = await client.PostSessionJsonAsync(payload, endpoint, cancellationToken);
        try
        {
            var result = root.Deserialize<LiveSessionResponse>(SessionJsonOptions)
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(result.Session?.Id)
                || result.Transport?.Type != request.Transport.Type
                || (result.Transport is LiveWebRtcResponseTransport rtc && string.IsNullOrWhiteSpace(rtc.Sdp)))
                throw new JsonException();
            return result;
        }
        catch (JsonException)
        {
            // Do not attach the response body or parser exception: they can contain secrets.
            throw new InvalidOperationException("Live session returned an invalid response.");
        }
    }

    internal static async Task<JsonObject> PostSessionJsonAsync(
        this HttpClient client, JsonObject payload, string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Realtime provisioning failed (HTTP {(int)response.StatusCode}).");
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(body) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Realtime provisioning returned an invalid JSON response.");
        }
    }

    internal static void ValidateGraph(object value)
    {
        if (value is string || value.GetType().IsValueType) return;
        if (value is IEnumerable items)
        {
            foreach (var item in items)
                if (item is not null) ValidateGraph(item);
            return;
        }

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), results, true))
            throw new ArgumentException("Invalid session configuration: " + string.Join(" ", results.Select(r => r.ErrorMessage)));
        foreach (var property in value.GetType().GetProperties())
            if (property.CanRead && property.GetIndexParameters().Length == 0 && property.GetValue(value) is { } child)
                ValidateGraph(child);
    }
}
