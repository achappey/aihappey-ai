using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private async Task<RealtimeTokenResponse> CreateGenericRealtimeTokenAsync(
        string model, RealtimeTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        cancellationToken.ThrowIfCancellationRequested();
        if (!RealtimeCapabilities.IsOpenAIRealtime(model))
            throw new NotSupportedException("Only OpenAI Realtime models support ephemeral Realtime tokens; GPT-Live uses sessions.");
        var payload = RealtimeSessionMapping.OpenAIPayload(model, request.SessionConfig);
        var tools = RealtimeSessionMapping.Tools(payload["session"]?["tools"]);
        ApplyAuthHeader();
        var result = await _client.PostSessionJsonAsync(payload, "v1/realtime/client_secrets", cancellationToken);
        return new RealtimeTokenResponse
        {
            Token = RealtimeSessionMapping.RequiredString(result, "value"),
            Url = "wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model),
            ExpiresAt = RealtimeSessionMapping.Expiry(result, "expires_at"),
            Tools = tools
        };
    }

    private async Task<RealtimeSessionResponse> CreateGenericRealtimeSessionAsync(
        string model, RealtimeSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Sdp);
        cancellationToken.ThrowIfCancellationRequested();
        if (RealtimeCapabilities.IsOpenAILive(model))
        {
            var payload = RealtimeSessionMapping.LivePayload(model, request.Sdp, request.SessionConfig);
            LiveSessionRequest native;
            try
            {
                // Do not silently discard native overrides that the strict Live contract cannot represent.
                var options = new JsonSerializerOptions(LiveSessionExtensions.SessionJsonOptions)
                {
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                };
                native = payload.Deserialize<LiveSessionRequest>(options) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new ArgumentException("Provider options do not form a valid Live session request.");
            }
            var response = await CreateLiveSessionAsync(native, cancellationToken);
            return new RealtimeSessionResponse
            {
                SessionId = response.Session.Id,
                Sdp = ((LiveWebRtcResponseTransport)response.Transport).Sdp
            };
        }
        if (!RealtimeCapabilities.IsOpenAIRealtime(model))
            throw new NotSupportedException("This model does not support OpenAI Realtime or Live sessions.");

        var realtime = RealtimeSessionMapping.OpenAIPayload(model, request.SessionConfig, request.Sdp);
        if (realtime.Any(p => p.Key is not ("session" or "sdp" or "transport")))
            throw new ArgumentException("Realtime calls accept provider options inside 'session' only.");
        var session = RealtimeSessionMapping.Object(realtime, "session");
        session.Remove("sdp");
        session.Remove("transport");
        _ = RealtimeSessionMapping.Tools(session["tools"]);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(request.Sdp, Encoding.UTF8, "application/sdp"), "sdp");
        multipart.Add(new StringContent(session.ToJsonString(), Encoding.UTF8, "application/json"), "session");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, "v1/realtime/calls") { Content = multipart };
        upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sdp"));
        ApplyAuthHeader();
        using var upstreamResponse = await _client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!upstreamResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Realtime call creation failed (HTTP {(int)upstreamResponse.StatusCode}).");
        var answer = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        var location = upstreamResponse.Headers.Location;
        var uri = location is null ? null : location.IsAbsoluteUri ? location : new Uri(_client.BaseAddress!, location);
        const string prefix = "/v1/realtime/calls/";
        var path = uri?.AbsolutePath;
        var callId = path?.StartsWith(prefix, StringComparison.Ordinal) == true ? path[prefix.Length..] : null;
        if (string.IsNullOrWhiteSpace(answer) || upstreamResponse.Content.Headers.ContentType?.MediaType != "application/sdp"
            || string.IsNullOrWhiteSpace(callId) || callId.Contains('/') || !string.IsNullOrEmpty(uri?.Query)
            || !string.IsNullOrEmpty(uri?.Fragment))
            throw new InvalidOperationException("Realtime call creation returned an invalid SDP answer or call location.");
        return new RealtimeSessionResponse { SessionId = callId, Sdp = answer };
    }
}
