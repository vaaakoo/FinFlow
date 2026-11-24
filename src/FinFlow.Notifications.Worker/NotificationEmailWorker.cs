using System.Net.Mail;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Notifications.Worker;

public sealed class NotificationEmailWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly string host;
    private readonly int port;
    private readonly ILogger<NotificationEmailWorker> logger;

    public NotificationEmailWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<NotificationEmailWorker> logger)
    {
        this.scopes = scopes;
        this.logger = logger;
        host = config["Mail:Host"] ?? throw new InvalidOperationException("Configure Mail:Host.");
        port = config.GetValue("Mail:Port", 1025);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SendNextAsync(stoppingToken))
                    continue;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Email queue unavailable.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<bool> SendNextAsync(CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Notifications.FromSqlInterpolated($"""
            SELECT * FROM "Notifications"
            WHERE "RecipientSubject" IS NOT NULL AND "EmailSentAt" IS NULL
                AND ("EmailRetryAt" IS NULL OR "EmailRetryAt" <= {now})
            ORDER BY "CreatedAt", "EventId" LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(token);
        if (rows.Count == 0)
            return false;

        var notification = rows[0];
        // Keycloak demo subjects map to synthetic addresses without storing or reading customer emails.
        if (!Guid.TryParse(notification.RecipientSubject, out var subject))
        {
            notification.EmailRetryAt = DateTimeOffset.MaxValue;
        }
        else
        {
            using var smtp = new SmtpClient(host, port);
            using var message = new MailMessage("notifications@finflow.example", $"{subject:N}@finflow.example",
                "FinFlow transfer received", $"A simulated transfer of {notification.AmountMinor} tetri was posted.");
            message.Headers.Add("Message-ID", $"<{notification.EventId:N}@finflow.example>");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await smtp.SendMailAsync(message, timeout.Token);
                notification.EmailSentAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Email for event {EventId} will retry after {FailureType}.",
                    notification.EventId, ex.GetType().Name);
                notification.EmailRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
        }

        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }
}
