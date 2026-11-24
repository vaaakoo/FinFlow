using FinFlow.Authentication;
using FinFlow.Observability;
using FinFlow.Messaging;
using FinFlow.Ledger.Contracts;
using FinFlow.Payments.Api;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
FinFlowTelemetry.Configure(builder, "payments");
builder.Services.AddFinFlowIdentity(builder.Configuration);
if (builder.Configuration["RabbitMq:Uri"] is string rabbitUri)
{
    builder.Services.AddSingleton(RabbitTopology.CreateFactory(rabbitUri));
    builder.Services.AddHostedService<LifecyclePublisher<PaymentsDb>>();
}
builder.Services.AddProblemDetails();
FinFlowTelemetry.AddDatabaseReadiness<PaymentsDb>(builder.Services);
builder.Services.AddHttpClient();
builder.Services.AddDbContext<PaymentsDb>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("Payments")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Payments.")));
builder.Services.AddSingleton(_ => GrpcChannel.ForAddress(builder.Configuration["LedgerPosting:Address"]
    ?? throw new InvalidOperationException("Configure LedgerPosting:Address.")));
builder.Services.AddSingleton(services => new LedgerPosting.LedgerPostingClient(services.GetRequiredService<GrpcChannel>()));
builder.Services.AddHostedService<PaymentWorker>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<PaymentsDb>().Database.MigrateAsync();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
FinFlowTelemetry.MapHealthEndpoints(app);
var payments = app.MapGroup("/customers/me/payments").RequireAuthorization(FinFlowIdentity.CustomerPolicy);
payments.MapPost("", PaymentEndpoints.Submit);
payments.MapGet("", PaymentEndpoints.List);
payments.MapGet("/{id:guid}", PaymentEndpoints.Get);
payments.MapPost("/{id:guid}/refund", PaymentEndpoints.Refund);
app.MapGet("/staff/payments", PaymentEndpoints.ListForStaff).RequireAuthorization(FinFlowIdentity.StaffPolicy);
app.Run();
