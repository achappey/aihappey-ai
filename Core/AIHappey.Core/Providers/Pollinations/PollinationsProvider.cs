using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Model.ChatCompletions;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IHttpContextAccessor _context;

    private readonly HttpClient _client;

    public PollinationsProvider(IApiKeyResolver keyResolver,
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _context = httpContextAccessor;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://text.pollinations.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public float? GetPriority() => 1;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(Pollinations).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default) =>
        await Task.FromResult<IEnumerable<Model>>(_context.HttpContext?.User?.Identity?.IsAuthenticated != true
        ? [new Model()
            {
                OwnedBy = nameof(Pollinations),
                Name = "Pollinations " + nameof(OpenAI),
                Type = "language",
                Id = nameof(OpenAI).ToLowerInvariant().ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Pollinations),
                Type = "language",
                Name = "Pollinations " + nameof(Mistral),
                Id = nameof(Mistral).ToLowerInvariant().ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Pollinations),
                Type = "image",
                Name = "Pollinations Flux",
                Id = "flux".ToModelId(GetIdentifier())
            }, new Model()
            {
                OwnedBy = nameof(Pollinations),
                Type = "image",
                Name = "Pollinations Turbo",
                Id = "turbo".ToModelId(GetIdentifier())
            }] : []);

    public async Task<CreateMessageResult> SamplingAsync(
     CreateMessageRequestParams chatRequest,
     CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var messages = chatRequest.Messages
            .SelectMany(m => m.Content.OfType<TextContentBlock>().Select(a => new
            {
                role = m.Role.ToString().ToLowerInvariant(),
                content = a.Text
            }))
            .ToArray();

        var payload = new
        {
            model = chatRequest.GetModel(),
            stream = false,
            temperature = chatRequest.Temperature,
            max_tokens = chatRequest.MaxTokens,
            messages
        };

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "openai")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        // expected shape: { id, model, choices: [ { message: { content } } ] }
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        var model = root
                  .GetProperty("model")
                  .GetString() ?? "";

        return new()
        {
            Model = model,
            Content = [new TextContentBlock()
            {
                Text = content
            }]
        };
    }

}