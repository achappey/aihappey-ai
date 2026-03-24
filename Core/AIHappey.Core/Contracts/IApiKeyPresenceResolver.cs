namespace AIHappey.Core.Contracts;

/// <summary>
/// Optional extension for API key resolvers that can differentiate between
/// explicitly configured/requested provider keys and implicit/default key resolution.
/// </summary>
public interface IApiKeyPresenceResolver
{
    bool HasConfiguredKey(string provider);
}

