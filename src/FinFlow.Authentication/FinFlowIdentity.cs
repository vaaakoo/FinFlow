using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinFlow.Authentication;

public static class FinFlowIdentity
{
    public const string StaffPolicy = "BankStaff";
    public const string CustomerPolicy = "Customer";
    public const string LedgerPosterPolicy = "LedgerPoster";

    public static void AddFinFlowIdentity(this IServiceCollection services, IConfiguration configuration)
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
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(StaffPolicy, policy => policy.RequireAuthenticatedUser()
                .RequireRole("Administrator", "BankOperator"))
            .AddPolicy(CustomerPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("Customer")
                .RequireAssertion(context => context.User.FindFirst("sub")?.Value.Length is > 0 and <= 128))
            .AddPolicy(LedgerPosterPolicy, policy => policy.RequireAuthenticatedUser().RequireRole("LedgerPoster")
                .RequireClaim("azp", "finflow-transfers"));
    }
}

