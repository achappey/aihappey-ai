namespace AIHappey.Core.Contracts;

public interface IApiKeyResolver
{
    string? Resolve(string provider);
}
