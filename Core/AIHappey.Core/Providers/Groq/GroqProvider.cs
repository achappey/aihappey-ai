using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider : IModelProvider
{
    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    public GroqProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.groq.com/");
        _client.DefaultRequestHeaders.Add("Groq-Model-Version", "latest");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Groq)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => GroqExtensions.Identifier();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await _client.GetAsync("openai/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return [.. data
            .EnumerateArray()
            .Select(m =>
            {
                var id = m.GetProperty("id").GetString() ?? string.Empty;
                var created = m.TryGetProperty("created", out var c) ? c.GetInt64() : 0;
                var ownedBy = m.TryGetProperty("owned_by", out var o) ? o.GetString() : string.Empty;

                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    OwnedBy = ownedBy!,
                    Created = created
                };
            })
            .OrderByDescending(r => r.Created)
            .DistinctBy(r => r.Id)];
    }


    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();


}