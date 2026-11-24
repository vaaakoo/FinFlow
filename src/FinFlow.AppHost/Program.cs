using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

var builder = DistributedApplication.CreateBuilder(args);

const string seedPassword = "finflow-local-demo";
const string postgresPassword = "demo-postgres";
const string rabbitPassword = "demo-rabbitmq";
const string transferSecret = "finflow-local-transfer-client";
const string paymentSecret = "finflow-local-payment-client";
const string issuer = "http://localhost:8082/realms/finflow";
const string rabbitUri = "amqp://finflow:demo-rabbitmq@localhost:15673/";

var postgres = builder.AddContainer("postgres", "postgres", "17-alpine")
    .WithEnvironment("POSTGRES_USER", "finflow")
    .WithEnvironment("POSTGRES_PASSWORD", postgresPassword)
    .WithEnvironment("POSTGRES_DB", "finflow_ledger")
    .WithBindMount("../../docker/init-databases.sql", "/docker-entrypoint-initdb.d/10-notifications.sql", isReadOnly: true)
    .WithEndpoint(port: 15432, targetPort: 5432, name: "postgres", isProxied: false)
    .WithHealthCheck("postgres");

builder.Services.AddHealthChecks().AddAsyncCheck("postgres", async token =>
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync("localhost", 15432, token);
        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
    }
    catch (System.Net.Sockets.SocketException)
    {
        return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy();
    }
});

var rabbit = builder.AddContainer("rabbitmq", "rabbitmq", "4-management-alpine")
    .WithEnvironment("RABBITMQ_DEFAULT_USER", "finflow")
    .WithEnvironment("RABBITMQ_DEFAULT_PASS", rabbitPassword)
    .WithEndpoint(port: 15673, targetPort: 5672, name: "amqp", isProxied: false);
var identity = builder.AddContainer("keycloak", "quay.io/keycloak/keycloak", "26.4.7")
    .WithArgs("start-dev", "--import-realm")
    .WithEnvironment("KC_HOSTNAME", "http://localhost:8082")
    .WithEnvironment("FINFLOW_DEMO_PASSWORD", seedPassword)
    .WithEnvironment("FINFLOW_TRANSFER_CLIENT_SECRET", transferSecret)
    .WithEnvironment("FINFLOW_PAYMENT_CLIENT_SECRET", paymentSecret)
    .WithBindMount("../../infrastructure/keycloak/finflow-realm.json",
        "/opt/keycloak/data/import/finflow-realm.json", isReadOnly: true)
    .WithHttpEndpoint(port: 8082, targetPort: 8080, name: "http", isProxied: false);
var valkey = builder.AddContainer("valkey", "valkey/valkey", "8-alpine")
    .WithEndpoint(port: 16379, targetPort: 6379, name: "valkey", isProxied: false);
var mail = builder.AddContainer("mailpit", "axllent/mailpit", "v1.34.0")
    .WithEndpoint(port: 11025, targetPort: 1025, name: "smtp", isProxied: false)
    .WithHttpEndpoint(port: 8025, targetPort: 8025, name: "http", isProxied: false);

var ledger = Api("ledger", "Ledger.Api", 8088)
    .WithEnvironment("ConnectionStrings__Ledger", Database("ledger"))
    .WithEnvironment("Identity__Enabled", "true")
    .WithEnvironment("RabbitMq__AuditEnabled", "true")
    .WithEnvironment("Kestrel__Endpoints__Http__Url", "http://127.0.0.1:8088")
    .WithEnvironment("Kestrel__Endpoints__Grpc__Url", "http://127.0.0.1:8089")
    .WithEnvironment("Kestrel__Endpoints__Grpc__Protocols", "Http2")
    .WithEndpoint(port: 8089, targetPort: 8089, name: "grpc", isProxied: false);
var customers = Api("customers", "Customers.Api", 8090)
    .WithEnvironment("ConnectionStrings__Customers", Database("customers"))
    .WithEnvironment("ConnectionStrings__Valkey", "localhost:16379,abortConnect=false,connectTimeout=1000")
    .WaitFor(valkey);
var transfers = Api("transfers", "Transfers.Api", 8091)
    .WithEnvironment("ConnectionStrings__Transfers", Database("transfers"))
    .WithEnvironment("LedgerPosting__Address", "http://localhost:8089")
    .WithEnvironment("LedgerPosting__TokenEndpoint", issuer + "/protocol/openid-connect/token")
    .WithEnvironment("LedgerPosting__ClientSecret", transferSecret)
    .WaitFor(ledger);
var payments = Api("payments", "Payments.Api", 8092)
    .WithEnvironment("ConnectionStrings__Payments", Database("payments"))
    .WithEnvironment("LedgerPosting__Address", "http://localhost:8089")
    .WithEnvironment("LedgerPosting__TokenEndpoint", issuer + "/protocol/openid-connect/token")
    .WithEnvironment("LedgerPosting__ClientSecret", paymentSecret)
    .WaitFor(ledger);
var audit = Api("audit", "Audit.Api", 8093)
    .WithEnvironment("ConnectionStrings__Audit", Database("audit"));
var notifications = Api("notifications", "Notifications.Worker", 8094)
    .WithEnvironment("ConnectionStrings__Notifications", Database("notifications"))
    .WithEnvironment("Identity__Enabled", "true")
    .WithEnvironment("Mail__Host", "localhost")
    .WithEnvironment("Mail__Port", "11025")
    .WaitFor(mail);

Api("gateway", "Gateway", 8081)
    .WithEnvironment("ReverseProxy__Clusters__ledger__Destinations__primary__Address", "http://localhost:8088/")
    .WithEnvironment("ReverseProxy__Clusters__customers__Destinations__primary__Address", "http://localhost:8090/")
    .WithEnvironment("ReverseProxy__Clusters__transfers__Destinations__primary__Address", "http://localhost:8091/")
    .WithEnvironment("ReverseProxy__Clusters__payments__Destinations__primary__Address", "http://localhost:8092/")
    .WithEnvironment("ReverseProxy__Clusters__audit__Destinations__primary__Address", "http://localhost:8093/")
    .WithEnvironment("ReverseProxy__Clusters__notifications__Destinations__primary__Address", "http://localhost:8094/")
    .WaitFor(ledger).WaitFor(customers).WaitFor(transfers).WaitFor(payments).WaitFor(audit).WaitFor(notifications);

builder.Build().Run();

IResourceBuilder<ProjectResource> Api(string name, string project, int port)
{
    var url = $"http://127.0.0.1:{port}";
    return builder.AddProject(name, $"../FinFlow.{project}/FinFlow.{project}.csproj", options => options.ExcludeLaunchProfile = true)
        .WithHttpEndpoint(port: port, targetPort: port, name: "http", isProxied: false)
        .WithEnvironment("ASPNETCORE_URLS", url)
        .WithEnvironment("Identity__Issuer", issuer)
        .WithEnvironment("Identity__AllowHttpMetadata", "true")
        .WithEnvironment("RabbitMq__Uri", rabbitUri)
        .WaitFor(postgres).WaitFor(rabbit).WaitFor(identity)
        .WithHttpHealthCheck("/health/live");
}

string Database(string name) => $"Host=localhost;Port=15432;Database=finflow_{name};Username=finflow;Password={postgresPassword}";
