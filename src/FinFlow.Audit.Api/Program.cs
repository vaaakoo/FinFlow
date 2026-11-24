using FinFlow.Audit.Api;
using FinFlow.Authentication;
using FinFlow.Observability;
using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
FinFlowTelemetry.Configure(builder, "audit");
builder.Services.AddFinFlowIdentity(builder.Configuration);
builder.Services.AddDbContext<AuditDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Audit")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Audit.")));
builder.Services.AddSingleton(_ => RabbitTopology.CreateFactory(builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("Configure RabbitMq:Uri.")));
builder.Services.AddHostedService<AuditConsumer>();
FinFlowTelemetry.AddDatabaseReadiness<AuditDb>(builder.Services);
builder.Services.AddProblemDetails();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.MigrateAsync();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
FinFlowTelemetry.MapHealthEndpoints(app);
app.MapGet("/staff/audit/transfers", AuditQueries.ListTransfers).RequireAuthorization(FinFlowIdentity.ReadAuditPolicy);
app.MapGet("/staff/audit/workflows", AuditQueries.ListLifecycle).RequireAuthorization(FinFlowIdentity.ReadAuditPolicy);
app.Run();

