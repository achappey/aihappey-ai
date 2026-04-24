using AIHappey.Core.Contracts;

namespace AIHappey.Core.Orchestration;

public sealed class NullMicrosoftGraphTokenResolver : IMicrosoftGraphTokenResolver
{
    public Task<string?> ResolveDelegatedAccessTokenAsync(
        string providerId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
