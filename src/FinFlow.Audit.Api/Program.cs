using FinFlow.Audit.Api;
using FinFlow.Authentication;
using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFinFlowIdentity(builder.Configuration);
builder.Services.AddDbContext<AuditDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Audit")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Audit.")));
builder.Services.AddSingleton(_ => RabbitTopology.CreateFactory(builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("Configure RabbitMq:Uri.")));
builder.Services.AddHostedService<AuditConsumer>();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<AuditDb>().Database.MigrateAsync();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/live");
app.MapGet("/staff/audit/transfers", AuditQueries.ListTransfers).RequireAuthorization(FinFlowIdentity.ReadAuditPolicy);
app.Run();

