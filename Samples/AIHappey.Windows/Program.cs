using AIHappey.HeaderAuth;
using AIHappey.Windows;

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

var app = builder.Build();
app.UseMiddleware<LocalHeaderDefaultsMiddleware>(headers);
app.MapHeaderAuthGateway();
app.Run();
