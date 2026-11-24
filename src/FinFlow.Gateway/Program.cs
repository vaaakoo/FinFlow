using System.Diagnostics;
using System.Threading.RateLimiting;
using FinFlow.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFinFlowIdentity(builder.Configuration);
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ApiRequests", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "anonymous", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1)
        }));
});
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    context.Response.Headers["X-Correlation-Id"] = traceId;
    var started = Stopwatch.GetTimestamp();
    try
    {
        await next(context);
    }
    finally
    {
        app.Logger.LogInformation("Gateway {Method} {Route} returned {Status} in {ElapsedMs} ms ({TraceId})",
            context.Request.Method, context.GetEndpoint()?.DisplayName, context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId);
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health/live");
app.MapReverseProxy();
app.Run();
