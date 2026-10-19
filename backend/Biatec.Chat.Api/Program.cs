using Biatec.Chat.Api.Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddContractsOpenApi();
var app = builder.Build();

app.MapContractCatalog();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Lets the integration tests use WebApplicationFactory<Program>.
public partial class Program;
