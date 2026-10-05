using AIHappey.HeaderAuth;
using AIHappey.Windows;
using AIHappey.Core.Diagnostics;

IReadOnlyDictionary<string, string> headers;
try
{
    headers = await LocalHeaderConfiguration.LoadAsync(LocalHeaderConfiguration.GetDefaultPath());
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5000");

builder.AddHeaderAuthGateway();
builder.Services.AddProviderDebugConsole();

var app = builder.Build();
app.UseMiddleware<LocalHeaderDefaultsMiddleware>(headers);
app.MapHeaderAuthGateway();
app.Run();
