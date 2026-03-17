using AIHappey.Core.Contracts;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Reflection;
using Azure.Identity;

namespace AIHappey.AzureAuth;

public class ConfigKeyResolver : IApiKeyResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly IReadOnlyDictionary<string, PropertyInfo> ProviderProperties = typeof(AIServiceConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.PropertyType == typeof(ProviderConfig))
        .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private readonly AIServiceConfig _config;
    private readonly IMemoryCache _cache;
    private readonly SecretClient? _secretClient;

    public ConfigKeyResolver(
        IOptions<AIServiceConfig> config,
        IOptions<KeyVaultOptions> keyVaultOptions,
        IOptions<AzureAdClientOptions> azureAdOptions,
        IMemoryCache cache)
    {
        _config = config.Value;
        _cache = cache;

        var vaultUri = keyVaultOptions.Value.VaultUri;
        var azureAd = azureAdOptions.Value;

        if (Uri.TryCreate(vaultUri, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(azureAd.TenantId)
            && !string.IsNullOrWhiteSpace(azureAd.ClientId)
            && !string.IsNullOrWhiteSpace(azureAd.Secret))
        {
            _secretClient = new SecretClient(
                uri,
                new ClientSecretCredential(azureAd.TenantId, azureAd.ClientId, azureAd.Secret));
        }
    }

    public string? Resolve(string provider)
    {
        if (!ProviderProperties.TryGetValue(provider, out var property))
        {
            return null;
        }

        var keyVaultApiKey = ResolveFromKeyVaultIfAvailable(property.Name);
        if (!string.IsNullOrWhiteSpace(keyVaultApiKey))
        {
            return keyVaultApiKey;
        }

        return (property.GetValue(_config) as ProviderConfig)?.ApiKey;
    }

    private string? ResolveFromKeyVaultIfAvailable(string secretName)
    {
        if (_secretClient is null)
        {
            return null;
        }

        var availableSecretNames = GetAvailableSecretNames();
        if (!availableSecretNames.Contains(secretName))
        {
            return null;
        }

        return _cache.GetOrCreate($"keyvault-apikey:{secretName}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            try
            {
                KeyVaultSecret secret = _secretClient.GetSecret(secretName).Value;
                return string.IsNullOrWhiteSpace(secret.Value) ? null : secret.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch
            {
                return null;
            }
        });
    }

    private HashSet<string> GetAvailableSecretNames()
    {
        if (_secretClient is null)
        {
            return [];
        }

        return _cache.GetOrCreate("keyvault-apikey:names", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var secretProperty in _secretClient.GetPropertiesOfSecrets())
                {
                    names.Add(secretProperty.Name);
                }
            }
            catch
            {
                return [];
            }

            return names;
        }) ?? [];
    }
}

