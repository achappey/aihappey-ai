namespace AIHappey.Core.Contracts;

public interface IMicrosoftGraphTokenResolver
{
    Task<string?> ResolveDelegatedAccessTokenAsync(
        string providerId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default);
}
