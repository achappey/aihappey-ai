
using AIHappey.HeaderAuth;

var builder = WebApplication.CreateBuilder(args);
builder.AddHeaderAuthGateway();

var app = builder.Build();
app.MapHeaderAuthGateway();

app.Run();
