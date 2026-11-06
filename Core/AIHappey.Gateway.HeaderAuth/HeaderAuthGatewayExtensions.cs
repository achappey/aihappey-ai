using System.Text.Json.Serialization;
using AIHappey.Common.MCP;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Http;
using AIHappey.Core.MCP;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.HeaderAuth.Controllers;
using AIHappey.HeaderAuth.Middleware;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace AIHappey.HeaderAuth;

/// <summary>
/// The portable HeaderAuth gateway. Hosts opt into deployment-specific
/// monitoring and credential defaults separately.
/// </summary>
public static class HeaderAuthGatewayExtensions
{
    public static WebApplicationBuilder AddHeaderAuthGateway(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
            options.Limits.MaxRequestBodySize = null;
        });

        var services = builder.Services;
        services.AddHttpContextAccessor();
        services.Configure<AIHappey.Core.Providers.Yandex.YandexProviderOptions>(
            builder.Configuration.GetSection("AIServices:Yandex"));
        services.Configure<AIHappey.Core.Providers.EdgeSpeak.EdgeSpeakProviderOptions>(
            builder.Configuration.GetSection("AIServices:EdgeSpeak"));
        services.Configure<EndUserIdHashingOptions>(builder.Configuration.GetSection("EndUserIdHashing"));
        services.Configure<ModelListingOptions>(builder.Configuration.GetSection("ModelListing"));
        services.Configure<ModelResolverOptions>(builder.Configuration);
        services.Configure<SkillProviderResolverOptions>(builder.Configuration.GetSection("SkillProviderResolver"));

        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .AllowAnyHeader()
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .WithExposedHeaders("WWW-Authenticate")));

        services.AddScoped<ModelProviderResolver>();
        services.AddScoped<IAIModelProviderResolver>(provider => provider.GetRequiredService<ModelProviderResolver>());
        services.AddScoped<IAISkillProviderResolver, SkillProviderResolver>();
        services.AddSingleton<HeaderApiKeySnapshot>();
        services.AddSingleton<IApiKeyResolver, HeaderApiKeyResolver>();
        services.AddSingleton<IEndUserIdResolver, HeaderEndUserIdResolver>();
        // Approval provenance is independent of provider keys in HeaderAuth.
        // Keep IEndUserIdResolver unchanged for provider identity/usage hashing.
        services.AddScoped(sp => new ToolApprovalSecurity(
            sp.GetRequiredService<IOptions<EndUserIdHashingOptions>>(),
            sp.GetRequiredService<IEndUserIdResolver>(), bindToUser: false));
        services.AddGatewayProviders();
        services.AddHttpClient();

        var mcpServers = CoreMcpDefinitions.GetDefinitions().ToArray();
        services.AddSingleton(new HeaderAuthGatewayEndpoints(mcpServers));
        services.AddMcpServers(mcpServers);
        services.AddControllers()
            .AddApplicationPart(typeof(ModelsController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

        return builder;
    }

    public static WebApplication MapHeaderAuthGateway(this WebApplication app)
    {
        var endpoints = app.Services.GetRequiredService<HeaderAuthGatewayEndpoints>();
        app.UseCors();
        app.UseMiddleware<MissingProviderCredentialMiddleware>();
        app.MapMcpEndpoints(endpoints.McpServers, false);
        app.MapMcpRegistry(endpoints.McpServers);
        app.MapControllers();
        return app;
    }

    private sealed record HeaderAuthGatewayEndpoints(McpServerDefinition[] McpServers);
}
