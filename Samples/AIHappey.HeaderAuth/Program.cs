
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(230);
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(230);
});

builder.Services.AddHttpContextAccessor();

// CORS for SPA (adjust origin as needed)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
              .AllowAnyHeader()
              .AllowAnyOrigin()
              .AllowAnyMethod()
              .WithExposedHeaders("WWW-Authenticate")
              .WithExposedHeaders("Mcp-Session-Id");
    });
});
builder.Services.AddSingleton<AIModelProviderResolver>();
builder.Services.AddSingleton<IApiKeyResolver, HeaderApiKeyResolver>();
builder.Services.AddProviders();
builder.Services.AddHttpClient();

builder.Services.AddControllers().AddJsonOptions(o =>
  {
      o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
  });;

var app = builder.Build();

app.UseCors();
app.MapControllers();

app.Run();
