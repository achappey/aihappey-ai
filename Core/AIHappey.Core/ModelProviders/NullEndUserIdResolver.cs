using AIHappey.Common.Model;

namespace AIHappey.Core.ModelProviders;

/// <summary>
/// Default resolver used when no host-specific end-user strategy is configured.
/// </summary>
public sealed class NullEndUserIdResolver : IEndUserIdResolver
{
    public string? Resolve(ChatRequest chatRequest) => null;
}

