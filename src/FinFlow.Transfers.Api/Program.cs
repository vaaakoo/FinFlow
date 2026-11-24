using FinFlow.Authentication;
using FinFlow.Observability;
using FinFlow.Messaging;
using FinFlow.Ledger.Contracts;
using FinFlow.Transfers.Api;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
FinFlowTelemetry.Configure(builder, "transfers");
builder.Services.AddFinFlowIdentity(builder.Configuration);
if (builder.Configuration["RabbitMq:Uri"] is string rabbitUri)
{
    builder.Services.AddSingleton(RabbitTopology.CreateFactory(rabbitUri));
    builder.Services.AddHostedService<LifecyclePublisher<TransfersDb>>();
}
builder.Services.AddProblemDetails();
FinFlowTelemetry.AddDatabaseReadiness<TransfersDb>(builder.Services);
builder.Services.AddHttpClient();
builder.Services.AddDbContext<TransfersDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Transfers")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Transfers.")));
builder.Services.AddSingleton(_ => new PostingOptions(
    builder.Configuration["LedgerPosting:TokenEndpoint"] ?? throw new InvalidOperationException("Configure the token endpoint."),
    builder.Configuration["LedgerPosting:ClientSecret"] ?? throw new InvalidOperationException("Configure the service client secret.")));
builder.Services.AddSingleton(_ => GrpcChannel.ForAddress(builder.Configuration["LedgerPosting:Address"]
    ?? throw new InvalidOperationException("Configure the ledger posting address.")));
builder.Services.AddSingleton(services => new LedgerPosting.LedgerPostingClient(services.GetRequiredService<GrpcChannel>()));
builder.Services.AddHostedService<PostingWorker>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<TransfersDb>().Database.MigrateAsync();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
FinFlowTelemetry.MapHealthEndpoints(app);
var transfers = app.MapGroup("/customers/me/transfers").RequireAuthorization(FinFlowIdentity.CustomerPolicy);
transfers.MapPost("", TransferEndpoints.Submit);
transfers.MapGet("", TransferEndpoints.List);
transfers.MapGet("/{id:guid}", TransferEndpoints.Get);
app.MapGet("/staff/transfers", TransferEndpoints.ListForStaff).RequireAuthorization(FinFlowIdentity.StaffPolicy);
app.Run();

