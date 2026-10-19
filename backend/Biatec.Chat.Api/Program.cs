using Biatec.Chat.Api.Config;
using Biatec.Chat.Api.Health;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBiatecOptions(builder.Configuration);
builder.Services.AddBiatecHealth();

var app = builder.Build();

// 200 Healthy when Redis and PostgreSQL answer, 503 Unhealthy otherwise. The body is the status only.
app.MapHealthChecks("/health");

app.Run();

public partial class Program;
