using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinFlow.Authentication;

public static class FinFlowIdentity
{
    public const string NotificationsHubPath = "/customers/me/notifications/live";
    public const string StaffPolicy = "BankStaff";
    public const string CustomerPolicy = "Customer";
    public const string LedgerPosterPolicy = "LedgerPoster";
    public const string PaymentPosterPolicy = "PaymentPoster";
    public const string ReadAuditPolicy = "ReadAudit";

    public static void AddFinFlowIdentity(this IServiceCollection services, IConfiguration configuration,
        bool allowNotificationsHubToken = false)
    {
        var issuer = configuration["Identity:Issuer"]
            ?? throw new InvalidOperationException("Configure Identity:Issuer.");
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = issuer;
            if (configuration["Identity:MetadataAddress"] is { Length: > 0 } metadataAddress)
                options.MetadataAddress = metadataAddress;
            options.Audience = "finflow-api";
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = !configuration.GetValue<bool>("Identity:AllowHttpMetadata");
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.RoleClaimType = "roles";
            if (allowNotificationsHubToken)
            {
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path == NotificationsHubPath
                            && context.Request.Query.TryGetValue("access_token", out var token) && token.Count == 1)
                            context.Token = token.ToString();
                        return Task.CompletedTask;
                    }
                };
            }
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(StaffPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireRole("Administrator", "BankOperator"))
            .AddPolicy(CustomerPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Customer")
                .RequireAssertion(context => context.User.FindFirst("sub")?.Value.Length is > 0 and <= 128))
            .AddPolicy(LedgerPosterPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("LedgerPoster")
                .RequireClaim("azp", "finflow-transfers"))
            .AddPolicy(PaymentPosterPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("PaymentPoster")
                .RequireClaim("azp", "finflow-payments"))
            .AddPolicy(ReadAuditPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireRole("Auditor", "Administrator"));
    }
}

