using FinFlow.Authentication;
using FinFlow.Observability;
using FinFlow.Messaging;
using FinFlow.Customers.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
FinFlowTelemetry.Configure(builder, "customers");
builder.Services.AddFinFlowIdentity(builder.Configuration);
if (builder.Configuration["RabbitMq:Uri"] is string rabbitUri)
{
    builder.Services.AddSingleton(RabbitTopology.CreateFactory(rabbitUri));
    builder.Services.AddHostedService<LifecyclePublisher<CustomersDb>>();
}
var valkey = builder.Configuration.GetConnectionString("Valkey");
if (string.IsNullOrWhiteSpace(valkey))
    builder.Services.AddDistributedMemoryCache();
else
    builder.Services.AddStackExchangeRedisCache(options => options.Configuration = valkey);
builder.Services.AddScoped<CustomerProfileCache>();
builder.Services.AddProblemDetails();
FinFlowTelemetry.AddDatabaseReadiness<CustomersDb>(builder.Services);
builder.Services.AddDbContext<CustomersDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Customers")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Customers.")));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CustomersDb>().Database.MigrateAsync();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

FinFlowTelemetry.MapHealthEndpoints(app);
var self = app.MapGroup("/customers/me").RequireAuthorization(FinFlowIdentity.CustomerPolicy);
self.MapPost("", CustomerEndpoints.Register);
self.MapGet("", CustomerEndpoints.GetOwn);
self.MapPut("", CustomerEndpoints.UpdateOwn);
var staff = app.MapGroup("/staff/customers").RequireAuthorization(FinFlowIdentity.StaffPolicy);
staff.MapGet("", CustomerEndpoints.List);
staff.MapGet("/{subject}", CustomerEndpoints.Get);
staff.MapPut("/{subject}/status", CustomerEndpoints.SetStatus);
app.Run();

