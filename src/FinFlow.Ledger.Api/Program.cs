using FinFlow.Messaging;
using FinFlow.Ledger.Api;
using FinFlow.Authentication;
using FinFlow.Observability;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
FinFlowTelemetry.Configure(builder, "ledger");
var secured = builder.Configuration.GetValue<bool>("Identity:Enabled");
if (secured)
{
    builder.Services.AddFinFlowIdentity(builder.Configuration);
    builder.Services.AddGrpc();
}
FinFlowTelemetry.AddDatabaseReadiness<LedgerDb>(builder.Services);
builder.Services.AddDbContext<LedgerDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Ledger.")));

builder.Services.AddSingleton(_ => RabbitTopology.CreateFactory(builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("Configure RabbitMq:Uri.")));
builder.Services.AddHostedService<OutboxPublisher>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<LedgerDb>().Database.MigrateAsync();

var ledger = app.MapGroup("");
if (secured)
{
    app.UseAuthentication();
    app.UseAuthorization();
    ledger.RequireAuthorization(FinFlowIdentity.StaffPolicy);
}

FinFlowTelemetry.MapHealthEndpoints(app);
ledger.MapGet("/accounts", Accounts.List);
ledger.MapGet("/transfers/{id:guid}", Transfers.Get);
ledger.MapPost("/transfers", Transfers.Create);
ledger.MapGet("/accounts/{accountId:guid}/statement", LedgerQueries.Statement);
ledger.MapGet("/staff/ledger/reconciliation", LedgerQueries.Reconcile);
if (secured)
{
    ledger.MapPost("/staff/ledger/accounts", Accounts.Open);
    var owned = app.MapGroup("/customers/me/accounts").RequireAuthorization(FinFlowIdentity.CustomerPolicy);
    owned.MapGet("", Accounts.ListOwned);
    owned.MapGet("/{accountId:guid}/statement", LedgerQueries.OwnStatement);
    app.MapGrpcService<LedgerPostingService>().RequireAuthorization();
}
app.Run();

public partial class Program;
