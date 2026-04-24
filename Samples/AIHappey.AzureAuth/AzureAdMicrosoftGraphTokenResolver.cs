using AIHappey.Core.Contracts;
using Microsoft.Identity.Web;

namespace AIHappey.AzureAuth;

public sealed class AzureAdMicrosoftGraphTokenResolver(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : IMicrosoftGraphTokenResolver
{
    public async Task<string?> ResolveDelegatedAccessTokenAsync(
        string providerId,
        IReadOnlyCollection<string> scopes,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(providerId, "microsoft", StringComparison.OrdinalIgnoreCase))
            return null;

        if (scopes.Count == 0)
            return null;

        if (!HasConfiguredConfidentialClientCredential(configuration))
            return null;

        using var scope = scopeFactory.CreateScope();
        var tokenAcquisition = scope.ServiceProvider.GetRequiredService<ITokenAcquisition>();

        return await tokenAcquisition.GetAccessTokenForUserAsync([.. scopes]);
    }

    private static bool HasConfiguredConfidentialClientCredential(IConfiguration configuration)
    {
        var clientSecret = configuration["AzureAd:ClientSecret"] ?? configuration["AzureAd:Secret"];
        if (IsConfigured(clientSecret))
            return true;

        if (IsConfigured(configuration["AzureAd:ClientCertificates:0:CertificateThumbprint"])
            || IsConfigured(configuration["AzureAd:ClientCertificates:0:CertificatePath"])
            || IsConfigured(configuration["AzureAd:ClientCertificates:0:KeyVaultCertificateName"])
            || IsConfigured(configuration["AzureAd:ClientAssertion"])
            || IsConfigured(configuration["AzureAd:ClientCredentials:0:ClientSecret"])
            || IsConfigured(configuration["AzureAd:ClientCredentials:0:CertificateThumbprint"])
            || IsConfigured(configuration["AzureAd:ClientCredentials:0:CertificatePath"])
            || IsConfigured(configuration["AzureAd:ClientCredentials:0:ManagedIdentityClientId"]))
        {
            return true;
        }

        return false;
    }

    private static bool IsConfigured(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.StartsWith('<')
           && !value.EndsWith('>');
}
