using AIHappey.Common.Model;

namespace AIHappey.Core.ModelProviders;

/// <summary>
/// Resolves an optional privacy-safe end-user identifier for upstream AI providers.
/// </summary>
public interface IEndUserIdResolver
{
    string? Resolve(ChatRequest chatRequest);
}

