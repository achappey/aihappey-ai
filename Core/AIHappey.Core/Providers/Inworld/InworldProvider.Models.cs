using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Providers.Fireworks;
using AIHappey.Core.Providers.DeepInfra;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var models = new List<Model>();

        // Hardcoded Inworld-native TTS models (docs)
        models.AddRange(InworldModels);

        // Routed provider models (Inworld -> upstream provider list)
        models.AddRange(await ListRoutedModels(cancellationToken));

        return models;
    }
    public static IReadOnlyList<Model> InworldModels =>
    [
        new()
        {
            Id = "inworld/inworld-tts-1.5-max",
            Name = "Llama Inworld TTS 1.5 Max",
            Type = "speech",
            OwnedBy = "Inworld AI",
            Description = "Flagship model, best balance of quality and speed, with enhanced timestamps."
        },
        new()
        {
            Id = "inworld/inworld-tts-1.5-mini",
            Name = "Llama Inworld TTS 1.5 Mini",
            Type = "speech",
            OwnedBy = "Inworld AI",
            Description = "Ultra-fast, most cost-efficient model, with enhanced timestamps."
        },
        new()
        {
            Id = "inworld/inworld-tts-1-max",
            Name = "Llama Inworld TTS Max",
            Type = "speech",
            OwnedBy = "Inworld AI",
            Description = "Most powerful previous generation model, with basic timestamps support."
        },
        new()
        {
            Id = "inworld/inworld-tts-1",
            Name = "Llama Inworld TTS",
            Type = "speech",
            OwnedBy = "Inworld AI",
            Description = "Fast previous generation model, with basic timestamps support."
        }
    ];

    private static readonly IReadOnlyDictionary<string, string> InworldProviderIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anthropic", "SERVICE_PROVIDER_ANTHROPIC" },
            { "cerebras", "SERVICE_PROVIDER_CEREBRAS" },
            { "deepinfra", "SERVICE_PROVIDER_DEEPINFRA" },
            { "fireworks", "SERVICE_PROVIDER_FIREWORKS" },
            { "google", "SERVICE_PROVIDER_GOOGLE" },
            { "groq", "SERVICE_PROVIDER_GROQ" },
            { "mistral", "SERVICE_PROVIDER_MISTRAL" },
            { "openai", "SERVICE_PROVIDER_OPENAI" },
            { "tenstorrent", "SERVICE_PROVIDER_TENSTORRENT" },
            { "xai", "SERVICE_PROVIDER_XAI" },
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Model>> InworldRoutedFallbackByProvider =
        new Dictionary<string, IReadOnlyList<Model>>(StringComparer.OrdinalIgnoreCase)
        {
            {
                "anthropic",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-opus-4-5", Name = "claude-opus-4-5", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-haiku-4-5", Name = "claude-haiku-4-5", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-sonnet-4-5", Name = "claude-sonnet-4-5", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-opus-4-1", Name = "claude-opus-4-1", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-opus-4-0", Name = "claude-opus-4-0", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-sonnet-4-0", Name = "claude-sonnet-4-0", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-3-7-sonnet-20250219", Name = "claude-3-7-sonnet-20250219", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-3-5-haiku-20241022", Name = "claude-3-5-haiku-20241022", Type = "language", OwnedBy = "Anthropic" },
                    new() { Id = "inworld/SERVICE_PROVIDER_ANTHROPIC/claude-3-5-haiku-latest", Name = "claude-3-5-haiku-latest", Type = "language", OwnedBy = "Anthropic" },
                ]
            },
            {
                "cerebras",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_CEREBRAS/llama3.1-8b", Name = "llama3.1-8b", Type = "language", OwnedBy = "Cerebras" },
                    new() { Id = "inworld/SERVICE_PROVIDER_CEREBRAS/llama-3.3-70b", Name = "llama-3.3-70b", Type = "language", OwnedBy = "Cerebras" },
                    new() { Id = "inworld/SERVICE_PROVIDER_CEREBRAS/gpt-oss-120b", Name = "gpt-oss-120b", Type = "language", OwnedBy = "Cerebras" },
                    new() { Id = "inworld/SERVICE_PROVIDER_CEREBRAS/qwen-3-32b", Name = "qwen-3-32b", Type = "language", OwnedBy = "Cerebras" },
                ]
            },
            {
                "deepinfra", [  ..DeepInfraProvider.DeepInfraLanguageModels
            .Where(a => a.Type == "language")
            .Select(m => new Model
            {
                Id = NormalizeToInworldId(m.Id, "SERVICE_PROVIDER_DEEPINFRA"),
                Name = m.Name,
                Type = m.Type,
                OwnedBy = m.OwnedBy ?? "DeepInfra",
                ContextWindow = m.ContextWindow,
                Created = m.Created
            })]
            },
            {
                "fireworks", [ ..FireworksProvider.FireworksModels
                    .Where(a => a.Type == "language")
                    .Select(m => new Model
                    {
                        Id = NormalizeToInworldId(m.Id, "SERVICE_PROVIDER_FIREWORKS"),
                        Name = m.Name,
                        Type = m.Type,
                        OwnedBy = m.OwnedBy ?? "Fireworks",
                        ContextWindow = m.ContextWindow,
                        Created = m.Created
                    })]
            },
            {
                "google",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_GOOGLE/gemini-3-pro-preview", Name = "gemini-3-pro-preview", Type = "language", OwnedBy = "Google" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GOOGLE/gemini-3-flash-preview", Name = "gemini-3-flash-preview", Type = "language", OwnedBy = "Google" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GOOGLE/gemini-2.5-pro", Name = "gemini-2.5-pro", Type = "language", OwnedBy = "Google" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GOOGLE/gemini-2.5-flash", Name = "gemini-2.5-flash", Type = "language", OwnedBy = "Google" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GOOGLE/gemini-2.5-flash-lite", Name = "gemini-2.5-flash-lite", Type = "language", OwnedBy = "Google" },
                ]
            },
            {
                "groq",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_GROQ/llama-3.3-70b-versatile", Name = "llama-3.3-70b-versatile", Type = "language", OwnedBy = "Groq" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GROQ/llama-3.1-8b-instant", Name = "llama-3.1-8b-instant", Type = "language", OwnedBy = "Groq" },
                    new() { Id = "inworld/SERVICE_PROVIDER_GROQ/openai/gpt-oss-20b", Name = "openai/gpt-oss-20b", Type = "language", OwnedBy = "Groq" },
                ]
            },
            {
                "mistral",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/mistral-large-latest", Name = "mistral-large-latest", Type = "language", OwnedBy = "Mistral" },
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/mistral-medium-latest", Name = "mistral-medium-latest", Type = "language", OwnedBy = "Mistral" },
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/mistral-small-latest", Name = "mistral-small-latest", Type = "language", OwnedBy = "Mistral" },
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/mistral-tiny-latest", Name = "mistral-tiny-latest", Type = "language", OwnedBy = "Mistral" },
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/pixtral-12b-2409", Name = "pixtral-12b-2409", Type = "language", OwnedBy = "Mistral" },
                    new() { Id = "inworld/SERVICE_PROVIDER_MISTRAL/ministral-8b-latest", Name = "ministral-8b-latest", Type = "language", OwnedBy = "Mistral" },
                ]
            },
            {
                "openai",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-5.2", Name = "gpt-5.2", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o", Name = "gpt-4o", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o-2024-11-20", Name = "gpt-4o-2024-11-20", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o-mini", Name = "gpt-4o-mini", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4-turbo", Name = "gpt-4-turbo", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4.1", Name = "gpt-4.1", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/gpt-3.5-turbo", Name = "gpt-3.5-turbo", Type = "language", OwnedBy = "OpenAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_OPENAI/davinci-002", Name = "davinci-002", Type = "language", OwnedBy = "OpenAI" },
                ]
            },
            {
                "tenstorrent",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_TENSTORRENT/tenstorrent/Llama-3.3-70B-Instruct", Name = "tenstorrent/Llama-3.3-70B-Instruct", Type = "language", OwnedBy = "Tenstorrent" },
                ]
            },
            {
                "xai",
                [
                    new() { Id = "inworld/SERVICE_PROVIDER_XAI/grok-4-0709", Name = "grok-4-0709", Type = "language", OwnedBy = "XAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_XAI/grok-4", Name = "grok-4", Type = "language", OwnedBy = "XAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_XAI/grok-3", Name = "grok-3", Type = "language", OwnedBy = "XAI" },
                    new() { Id = "inworld/SERVICE_PROVIDER_XAI/grok-3-mini", Name = "grok-3-mini", Type = "language", OwnedBy = "XAI" },
                ]
            },
        };

    private static string NormalizeToInworldId(string fullId, string provider)
    {
        var split = fullId.SplitModelId();
        return $"inworld/{provider}/{split.Model}";
    }

    private async Task<IEnumerable<Model>> ListRoutedModels(CancellationToken cancellationToken)
    {
        var models = new List<Model>();

        var providers = _serviceProvider
            .GetServices<IModelProvider>()
            .Where(p => !string.Equals(p.GetIdentifier(), GetIdentifier(), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var provider in providers)
        {
            if (string.Equals(provider.GetIdentifier(), GetIdentifier(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (!InworldProviderIds.ContainsKey(provider.GetIdentifier()))
                continue;

            try
            {
                var upstreamModels = await provider.ListModels(cancellationToken);

                foreach (var model in upstreamModels)
                {
                    var mapped = new Model
                    {
                        Id = NormalizeToInworldId(model.Id, InworldProviderIds[provider.GetIdentifier()]),
                        Name = model.Name,
                        Description = model.Description,
                        ContextWindow = model.ContextWindow,
                        MaxTokens = model.MaxTokens,
                        Pricing = model.Pricing,
                        Created = model.Created,
                        Tags = model.Tags,
                        Type = model.Type,
                        OwnedBy = model.OwnedBy
                    };

                    if (string.IsNullOrWhiteSpace(mapped.Type))
                        mapped.Type = mapped.Id.GuessModelType();

                    if (!string.IsNullOrWhiteSpace(mapped.Id))
                        models.Add(mapped);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // If a provider fails, fall back to docs examples for that provider
                if (InworldRoutedFallbackByProvider.TryGetValue(provider.GetIdentifier(), out var fallback))
                    models.AddRange(fallback);
            }
        }

        return models.Where(a => a.Type == "language");
    }
}
