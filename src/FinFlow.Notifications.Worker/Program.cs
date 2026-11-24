using FinFlow.Messaging;
using FinFlow.Notifications.Worker;
using Microsoft.EntityFrameworkCore;

using var host = NotificationsApplication.Build(args);
await using (var scope = host.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<NotificationsDb>().Database.MigrateAsync();
await host.StartAsync();
await host.WaitForShutdownAsync();

namespace FinFlow.Notifications.Worker
{
    public static class NotificationsApplication
    {
        public static IHost Build(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddDbContext<NotificationsDb>(options => options.UseNpgsql(
                builder.Configuration.GetConnectionString("Notifications")
                ?? throw new InvalidOperationException("Configure ConnectionStrings:Notifications.")));
            builder.Services.AddSingleton(_ => RabbitTopology.CreateFactory(builder.Configuration["RabbitMq:Uri"]
                ?? throw new InvalidOperationException("Configure RabbitMq:Uri.")));
            builder.Services.AddHostedService<NotificationConsumer>();
            return builder.Build();
        }
    }
}

