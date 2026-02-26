using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    private async Task<ResponseResult> ResponsesAsyncInternal(ResponseRequest options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        var outgoingModel = NormalizeOutgoingModel(options.Model);

        var (raw, statusCode, reasonPhrase, isSuccess) = await SendResponsesRequestAsync(
            options,
            outgoingModel,
            stream: false,
            cancellationToken);

        if (!isSuccess)
            throw new HttpRequestException($"Vapi responses error: {(int)statusCode} {reasonPhrase}: {raw}");

        var result = JsonSerializer.Deserialize<ResponseResult>(raw, ResponseJson.Default)
                     ?? throw new InvalidOperationException("Vapi responses payload could not be deserialized.");

        result.Model = NormalizeIncomingModel(result.Model, options.Model);

        var chatId = TryExtractChatId(raw);
        var cleanupWarning = await TryDeleteChatAsync(chatId, cancellationToken);
        if (cleanupWarning is not null)
            AddCleanupWarning(result, cleanupWarning);

        return result;
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsyncInternal(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        var outgoingModel = NormalizeOutgoingModel(options.Model);
        var contentOnlyForAllRoles = false;

        HttpResponseMessage resp;
        while (true)
        {
            var payload = BuildResponsesPayload(options, outgoingModel, contentOnlyForAllRoles);
            payload["stream"] = true;

            var json = JsonSerializer.Serialize(payload, ResponseJson.Default);

            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/responses")
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };

            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (resp.IsSuccessStatusCode)
                break;

            var rawErr = await resp.Content.ReadAsStringAsync(cancellationToken);
            resp.Dispose();

            if (!contentOnlyForAllRoles && ShouldRetryWithContentOnlyInput(rawErr))
            {
                contentOnlyForAllRoles = true;
                continue;
            }

            throw new HttpRequestException($"Vapi responses streaming error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {rawErr}");
        }

        using (resp)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            string? eventType = null;
            string? chatId = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;

                if (line.Length == 0)
                {
                    eventType = null;
                    continue;
                }

                if (line.StartsWith(':'))
                    continue;

                if (line.StartsWith("event: ", StringComparison.OrdinalIgnoreCase))
                {
                    eventType = line["event: ".Length..].Trim();
                    continue;
                }

                if (!line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var payloadData = line["data: ".Length..].Trim();
                if (payloadData is "[DONE]" or "[done]")
                    break;

                chatId ??= TryExtractChatId(payloadData);

                if (!TryParseResponseStreamPart(eventType, payloadData, out var part))
                    continue;

                yield return part!;
            }

            _ = await TryDeleteChatAsync(chatId, cancellationToken);
        }
    }

    private async Task<(string Raw, System.Net.HttpStatusCode StatusCode, string? ReasonPhrase, bool IsSuccess)> SendResponsesRequestAsync(
        ResponseRequest options,
        string outgoingModel,
        bool stream,
        CancellationToken cancellationToken)
    {
        var contentOnlyForAllRoles = false;

        while (true)
        {
            var payload = BuildResponsesPayload(options, outgoingModel, contentOnlyForAllRoles);
            payload["stream"] = stream;

            var json = JsonSerializer.Serialize(payload, ResponseJson.Default);

            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/responses")
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };

            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (resp.IsSuccessStatusCode)
                return (raw, resp.StatusCode, resp.ReasonPhrase, true);

            if (!contentOnlyForAllRoles && ShouldRetryWithContentOnlyInput(raw))
            {
                contentOnlyForAllRoles = true;
                continue;
            }

            return (raw, resp.StatusCode, resp.ReasonPhrase, false);
        }
    }

    private string NormalizeOutgoingModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Model is required.");

        var trimmed = model.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var split = trimmed.SplitModelId();
            if (string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase))
                return split.Model;
        }

        return trimmed;
    }

    private string NormalizeIncomingModel(string? responseModel, string? requestModel)
    {
        if (!string.IsNullOrWhiteSpace(responseModel))
        {
            var model = responseModel.Trim();
            if (model.Contains('/', StringComparison.Ordinal))
            {
                var split = model.SplitModelId();
                if (string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase))
                    return model;
            }

            return model.ToModelId(GetIdentifier());
        }

        if (!string.IsNullOrWhiteSpace(requestModel))
            return requestModel!;

        return string.Empty;
    }

}

