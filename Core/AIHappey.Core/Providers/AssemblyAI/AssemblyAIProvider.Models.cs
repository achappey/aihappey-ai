using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider : IModelProvider
{

    public static IReadOnlyList<Model> AssemblyAIModels =>
    [
        new() { Id = "universal-3-pro".ToModelId(nameof(AssemblyAI).ToLowerInvariant()),
            Name = "universal-3-pro",
            Type = "transcription", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "universal-2".ToModelId(nameof(AssemblyAI).ToLowerInvariant()),
            Name = "universal-2",
            Type = "transcription", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "universal-streaming-english".ToModelId(nameof(AssemblyAI).ToLowerInvariant()),
            Name = "universal-streaming-english",
            Tags = ["real-time"],
            Type = "transcription", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "universal-streaming-multilingual".ToModelId(nameof(AssemblyAI).ToLowerInvariant()),
            Name = "universal-streaming-multilingual",
            Tags = ["real-time"],
            Type = "transcription", OwnedBy = nameof(AssemblyAI) },
    ];

    /// <summary>
    /// AssemblyAI LLM Gateway models.
    /// Docs: https://llm-gateway.assemblyai.com/v1/chat/completions
    /// </summary>
    public static IReadOnlyList<Model> AssemblyAILlmGatewayModels =>
    [
        // ===== Anthropic Claude =====
        new() { Id = "claude-sonnet-4-5-20250929".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "claude-sonnet-4-5-20250929", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "claude-sonnet-4-20250514".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "claude-sonnet-4-20250514", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "claude-opus-4-20250514".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "claude-opus-4-20250514", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "claude-haiku-4-5-20251001".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "claude-haiku-4-5-20251001", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "claude-3-haiku-20240307".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "claude-3-haiku-20240307", Type = "language", OwnedBy = nameof(AssemblyAI) },

        // ===== OpenAI GPT =====
        new() { Id = "gpt-5.2".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-5.2", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-5.1".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-5.1", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-5".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-5", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-5-nano".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-5-nano", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-5-mini".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-5-mini", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-4.1".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-4.1", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-oss-120b".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-oss-120b", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gpt-oss-20b".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gpt-oss-20b", Type = "language", OwnedBy = nameof(AssemblyAI) },

        // ===== Google Gemini =====
        new() { Id = "gemini-3-pro-preview".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gemini-3-pro-preview", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gemini-3-flash-preview".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gemini-3-flash-preview", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gemini-2.5-pro".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gemini-2.5-pro", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gemini-2.5-flash".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gemini-2.5-flash", Type = "language", OwnedBy = nameof(AssemblyAI) },
        new() { Id = "gemini-2.5-flash-lite".ToModelId(nameof(AssemblyAI).ToLowerInvariant()), Name = "gemini-2.5-flash-lite", Type = "language", OwnedBy = nameof(AssemblyAI) },
    ];

    public static IReadOnlyList<Model> AssemblyAIAllModels =>
        [.. AssemblyAIModels, .. AssemblyAILlmGatewayModels];


}
