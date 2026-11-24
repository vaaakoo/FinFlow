using FinFlow.Authentication;
using FinFlow.Customers.Api;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFinFlowIdentity(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
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

app.MapHealthChecks("/health/live");
var self = app.MapGroup("/customers/me").RequireAuthorization(FinFlowIdentity.CustomerPolicy);
self.MapPost("", CustomerEndpoints.Register);
self.MapGet("", CustomerEndpoints.GetOwn);
self.MapPut("", CustomerEndpoints.UpdateOwn);
var staff = app.MapGroup("/staff/customers").RequireAuthorization(FinFlowIdentity.StaffPolicy);
staff.MapGet("", CustomerEndpoints.List);
staff.MapGet("/{subject}", CustomerEndpoints.Get);
staff.MapPut("/{subject}/status", CustomerEndpoints.SetStatus);
app.Run();

