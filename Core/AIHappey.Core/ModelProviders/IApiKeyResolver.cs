namespace AIHappey.Core.ModelProviders;

public interface IApiKeyResolver
{
    string? Resolve(string provider);
}
