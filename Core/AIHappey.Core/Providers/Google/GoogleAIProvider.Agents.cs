using AIHappey.Interactions;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string DeepResearchAgentPrefix = "deep-research";
    private const string InteractionsRelativeUrl = "v1beta/interactions";
    private static readonly TimeSpan GoogleAgentPollingInterval = TimeSpan.FromSeconds(10);
    private static readonly HashSet<string> TerminalInteractionStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "completed",
        "failed",
        "cancelled",
        "canceled",
        "expired"
    };

    private static readonly JsonSerializerOptions GoogleAgentJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static bool TryNormalizeGoogleAgentRequest(InteractionRequest request, out string agent, bool stream = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        agent = NormalizeGoogleModelOrAgentId(request.Agent);
        if (string.IsNullOrWhiteSpace(agent))
            agent = NormalizeGoogleModelOrAgentId(request.Model);

        if (!IsDeepResearchAgent(agent))
            return false;

        request.Agent = agent;
        request.Model = null;
        request.Background = true;
        request.Store = true;
        request.Stream = stream ? true : null;
        request.GenerationConfig = null;
        request.AdditionalProperties?.Remove("generation_config");

        if (stream && request.AgentConfig is null)
        {
            request.AgentConfig = new InteractionDeepResearchAgentConfig
            {
                ThinkingSummaries = "auto"
            };
        }

        return true;
    }

    private static bool IsDeepResearchAgent(string? value)
        => NormalizeGoogleModelOrAgentId(value).StartsWith(DeepResearchAgentPrefix, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGoogleModelOrAgentId(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        const string modelsPrefix = "models/";
        if (text.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[modelsPrefix.Length..];

        var providerPrefix = GoogleExtensions.Identifier() + "/";
        if (text.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[providerPrefix.Length..];

        return text;
    }

    private async Task<Interaction> CreateGoogleAgentInteraction(
        InteractionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("application/json");
        var payload = JsonSerializer.SerializeToElement(request, GoogleAgentJsonOptions);
        httpRequest.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowGoogleAgentApiIfNotSuccess(response, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Interaction>(body, GoogleAgentJsonOptions)
               ?? throw new InvalidOperationException("Empty JSON response for Google agent interaction create.");
    }

    private async IAsyncEnumerable<InteractionStreamEventPart> CreateGoogleAgentInteractionStream(
        InteractionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        var payload = JsonSerializer.SerializeToElement(request, GoogleAgentJsonOptions);
        httpRequest.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowGoogleAgentApiIfNotSuccess(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0)
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                yield break;

            InteractionStreamEventPart? evt;
            try
            {
                evt = JsonSerializer.Deserialize<InteractionStreamEventPart>(data, GoogleAgentJsonOptions);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse Google agent SSE json event: {data}", ex);
            }

            NormalizeGoogleAgentStreamEvent(evt);

            if (evt is not null)
                yield return evt;
        }
    }

    private async Task<Interaction> GetGoogleAgentInteraction(
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{InteractionsRelativeUrl}/{Uri.EscapeDataString(interactionId)}");
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await ThrowGoogleAgentApiIfNotSuccess(response, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Interaction>(body, GoogleAgentJsonOptions)
               ?? throw new InvalidOperationException($"Empty JSON response for Google agent interaction '{interactionId}'.");
    }

    private async Task DeleteGoogleAgentInteraction(
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"{InteractionsRelativeUrl}/{Uri.EscapeDataString(interactionId)}");
            using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
                return;

            var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to delete Google agent interaction {InteractionId}. HTTP {StatusCode} {ReasonPhrase}: {Body}",
                interactionId,
                (int)response.StatusCode,
                response.ReasonPhrase,
                body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete Google agent interaction {InteractionId}.", interactionId);
        }
    }

    private async Task<Interaction> PollGoogleAgentInteraction(
        string interactionId,
        CancellationToken cancellationToken = default)
    {
        Interaction? current = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            current = await GetGoogleAgentInteraction(interactionId, cancellationToken);
            NormalizeGoogleAgentInteraction(current);

            if (IsTerminalInteractionStatus(current.Status))
                return current;

            await Task.Delay(GoogleAgentPollingInterval, cancellationToken);
        }

        return current ?? throw new OperationCanceledException(cancellationToken);
    }

    private async IAsyncEnumerable<InteractionStreamEventPart> PollGoogleAgentInteractionAsStream(
        string interactionId,
        Interaction initialInteraction,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        NormalizeGoogleAgentInteraction(initialInteraction);

        yield return new InteractionStartEvent
        {
            EventId = CreateGoogleAgentEventId(),
            Interaction = initialInteraction
        };

        string? lastStatus = initialInteraction.Status;

        if (!string.IsNullOrWhiteSpace(lastStatus))
        {
            yield return new InteractionStatusUpdateEvent
            {
                EventId = CreateGoogleAgentEventId(),
                InteractionId = interactionId,
                Status = lastStatus
            };
        }

        Interaction current = initialInteraction;
        while (!IsTerminalInteractionStatus(current.Status))
        {
            await Task.Delay(GoogleAgentPollingInterval, cancellationToken);

            current = await GetGoogleAgentInteraction(interactionId, cancellationToken);
            NormalizeGoogleAgentInteraction(current);

            if (!string.Equals(lastStatus, current.Status, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(current.Status))
            {
                lastStatus = current.Status;
                yield return new InteractionStatusUpdateEvent
                {
                    EventId = CreateGoogleAgentEventId(),
                    InteractionId = interactionId,
                    Status = current.Status
                };
            }
        }

        if (string.Equals(current.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var index = 0;
            foreach (var output in current.Outputs ?? [])
            {
                yield return new InteractionContentStartEvent
                {
                    EventId = CreateGoogleAgentEventId(),
                    Index = index,
                    Content = CreateGoogleAgentStreamStartContent(output)
                };

                foreach (var delta in CreateGoogleAgentStreamDeltas(output))
                {
                    yield return new InteractionContentDeltaEvent
                    {
                        EventId = CreateGoogleAgentEventId(),
                        Index = index,
                        Delta = delta
                    };
                }

                yield return new InteractionContentStopEvent
                {
                    EventId = CreateGoogleAgentEventId(),
                    Index = index
                };

                index++;
            }
        }
        else
        {
            yield return new InteractionErrorEvent
            {
                EventId = CreateGoogleAgentEventId(),
                Error = new InteractionErrorInfo
                {
                    Code = current.Status,
                    Message = $"Google agent interaction '{interactionId}' ended with status '{current.Status}'."
                }
            };
        }

        yield return new InteractionCompleteEvent
        {
            EventId = CreateGoogleAgentEventId(),
            Interaction = current
        };
    }

    private static InteractionContent CreateGoogleAgentStreamStartContent(InteractionContent output)
        => output switch
        {
            InteractionTextContent => new InteractionTextContent(),
            InteractionThoughtContent thought => new InteractionThoughtContent
            {
                Signature = thought.Signature,
                Summary = null
            },
            InteractionImageContent image => new InteractionImageContent
            {
                MimeType = image.MimeType,
                Resolution = image.Resolution
            },
            InteractionAudioContent audio => new InteractionAudioContent
            {
                MimeType = audio.MimeType,
                Rate = audio.Rate,
                Channels = audio.Channels
            },
            InteractionDocumentContent document => new InteractionDocumentContent
            {
                MimeType = document.MimeType
            },
            InteractionVideoContent video => new InteractionVideoContent
            {
                MimeType = video.MimeType,
                Resolution = video.Resolution
            },
            _ => output
        };

    private static IEnumerable<InteractionContentDeltaData> CreateGoogleAgentStreamDeltas(InteractionContent output)
    {
        switch (output)
        {
            case InteractionTextContent text:
                yield return new InteractionContentDeltaData
                {
                    Type = "text",
                    Text = text.Text ?? string.Empty
                };
                break;

            case InteractionThoughtContent thought:
                var summaryText = FlattenGoogleAgentContentText(thought.Summary);
                if (!string.IsNullOrWhiteSpace(summaryText))
                {
                    yield return new InteractionContentDeltaData
                    {
                        Type = "thought_summary",
                        Text = summaryText
                    };
                }
                break;

            case InteractionImageContent image:
                yield return new InteractionContentDeltaData
                {
                    Type = "image",
                    Text = image.Data ?? image.Uri ?? string.Empty,
                    AdditionalProperties = CreateGoogleAgentDeltaProperties(new Dictionary<string, object?>
                    {
                        ["data"] = image.Data,
                        ["uri"] = image.Uri,
                        ["mime_type"] = image.MimeType
                    })
                };
                break;

            case InteractionAudioContent audio:
                yield return new InteractionContentDeltaData
                {
                    Type = "audio",
                    Text = audio.Data ?? audio.Uri ?? string.Empty,
                    AdditionalProperties = CreateGoogleAgentDeltaProperties(new Dictionary<string, object?>
                    {
                        ["data"] = audio.Data,
                        ["uri"] = audio.Uri,
                        ["mime_type"] = audio.MimeType
                    })
                };
                break;
        }
    }

    private static Dictionary<string, JsonElement>? CreateGoogleAgentDeltaProperties(Dictionary<string, object?> properties)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in properties)
        {
            if (value is not null)
                result[key] = JsonSerializer.SerializeToElement(value, GoogleAgentJsonOptions);
        }

        return result.Count == 0 ? null : result;
    }

    private static string FlattenGoogleAgentContentText(IEnumerable<InteractionContent>? content)
        => string.Join("\n", (content ?? []).OfType<InteractionTextContent>().Select(a => a.Text).Where(a => !string.IsNullOrWhiteSpace(a))!);

    private static bool IsTerminalInteractionStatus(string? status)
        => !string.IsNullOrWhiteSpace(status) && TerminalInteractionStatuses.Contains(status);

    private static string CreateGoogleAgentEventId()
        => $"google-agent-{Guid.NewGuid():N}";

    private static void NormalizeGoogleAgentInteraction(Interaction interaction)
    {
        if (!string.IsNullOrWhiteSpace(interaction.Model))
            interaction.Model = NormalizeGoogleModelOrAgentId(interaction.Model);

        if (!string.IsNullOrWhiteSpace(interaction.Agent))
            interaction.Agent = NormalizeGoogleModelOrAgentId(interaction.Agent);
    }

    private static void NormalizeGoogleAgentStreamEvent(InteractionStreamEventPart? evt)
    {
        switch (evt)
        {
            case InteractionStartEvent { Interaction: not null } start:
                NormalizeGoogleAgentInteraction(start.Interaction);
                break;
            case InteractionCompleteEvent { Interaction: not null } complete:
                NormalizeGoogleAgentInteraction(complete.Interaction);
                break;
        }
    }

    private static async Task ThrowGoogleAgentApiIfNotSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }
}
