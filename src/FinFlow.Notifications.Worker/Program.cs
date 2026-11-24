using FinFlow.Authentication;
using FinFlow.Observability;
using FinFlow.Messaging;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Notifications.Worker;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        await using var app = NotificationsApplication.Build(args);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<NotificationsDb>().Database.MigrateAsync();
        await app.RunAsync();
    }
}

public static class NotificationsApplication
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        FinFlowTelemetry.Configure(builder, "notifications");
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        var secured = builder.Configuration.GetValue<bool>("Identity:Enabled");
        if (secured)
            builder.Services.AddFinFlowIdentity(builder.Configuration, allowNotificationsHubToken: true);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IUserIdProvider, NotificationUserIdProvider>();
        FinFlowTelemetry.AddDatabaseReadiness<NotificationsDb>(builder.Services);
        builder.Services.AddDbContext<NotificationsDb>(options => options.UseNpgsql(
            builder.Configuration.GetConnectionString("Notifications")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:Notifications.")));
        builder.Services.AddSingleton(_ => RabbitTopology.CreateFactory(builder.Configuration["RabbitMq:Uri"]
            ?? throw new InvalidOperationException("Configure RabbitMq:Uri.")));
        builder.Services.AddHostedService<NotificationConsumer>();
        if (!string.IsNullOrEmpty(builder.Configuration["Mail:Host"]))
            builder.Services.AddHostedService<NotificationEmailWorker>();

        var app = builder.Build();
        FinFlowTelemetry.MapHealthEndpoints(app);
        if (secured)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            var notifications = app.MapGroup("/customers/me/notifications")
                .RequireAuthorization(FinFlowIdentity.CustomerPolicy);
            notifications.MapGet("", NotificationEndpoints.List);
            notifications.MapPut("/{id:guid}/read", NotificationEndpoints.MarkRead);
            app.MapHub<NotificationHub>(FinFlowIdentity.NotificationsHubPath,
                options => options.CloseOnAuthenticationExpiration = true)
                .RequireAuthorization(FinFlowIdentity.CustomerPolicy);
        }
        return app;
    }
}
