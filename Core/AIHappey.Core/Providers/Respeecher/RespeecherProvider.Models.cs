using System.Text.Json;
using System.Net;
using System.Runtime.ExceptionServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Respeecher;

public sealed partial class RespeecherProvider
{
    // Space documents model paths, not a model-listing API. Voices remain account-specific.
    private static readonly string[] ModelPaths = ["en-rt", "ua-rt", "ar-rt", "de-rt", "pt-br-rt"];

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        var models = new List<Model>();
        ExceptionDispatchInfo? firstFailure = null;
        var successfulRequests = 0;
        foreach (var model in ModelPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{Uri.EscapeDataString(model)}/voices");
                request.Headers.Add("X-API-Key", key);
                using var response = await _client.SendAsync(request, cancellationToken);
                await EnsureSuccessAsync(response, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Expected a Respeecher voices array.");
                var languageModels = new List<Model>();
                foreach (var voice in document.RootElement.EnumerateArray())
                {
                    var id = GetString(voice, "id");
                    if (string.IsNullOrWhiteSpace(id))
                        throw new JsonException("Expected a Respeecher voice id.");
                    languageModels.Add(new Model
                    {
                        Id = $"{GetIdentifier()}/{model}/{id}",
                        Name = GetString(voice, "full_name") ?? id,
                        OwnedBy = "Respeecher",
                        Type = "speech",
                        Metadata = new() { [GetIdentifier()] = voice.Clone() }
                    });
                }
                cancellationToken.ThrowIfCancellationRequested();
                models.AddRange(languageModels);
                successfulRequests++;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (error is HttpRequestException { StatusCode: not HttpStatusCode.Unauthorized and not HttpStatusCode.Forbidden }
                    or JsonException or OperationCanceledException))
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (successfulRequests == 0)
            firstFailure?.Throw();
        return models.DistinctBy(model => model.Id).ToArray();
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
