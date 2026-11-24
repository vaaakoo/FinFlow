using System.Security.Claims;
using FinFlow.Messaging;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Customers.Api;

public static class CustomerEndpoints
{
    public static async Task<IResult> Register(ClaimsPrincipal user, ProfileInput input, CustomersDb db,
        CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        var name = input.DisplayName?.Trim();
        if (name is null || name.Length is < 1 or > 100)
            return InvalidName();

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Profiles" ("Subject", "DisplayName", "Kyc", "Active")
            VALUES ({subject}, {name}, 'Pending', true)
            ON CONFLICT ("Subject") DO NOTHING
            """, token);
        if (inserted == 0)
            return Results.Problem(statusCode: 409, detail: "A profile already exists for this identity.");

        db.Add(LifecycleOutbox.Create("customer.registered", subject));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Results.Created("/customers/me", new CustomerView(subject, name, nameof(KycStatus.Pending), true));
    }

    public static async Task<IResult> GetOwn(ClaimsPrincipal user, CustomersDb db,
        CustomerProfileCache cache, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        var cached = await cache.GetAsync(subject, token);
        if (cached is not null)
            return Results.Ok(cached);

        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.Subject == subject, token);
        if (profile is null)
            return Results.NotFound();
        var view = profile.View();
        await cache.SetAsync(view, token);
        return Results.Ok(view);
    }

    public static async Task<IResult> Get(string subject, CustomersDb db, CancellationToken token)
    {
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.Subject == subject, token);
        if (profile is null)
            return Results.NotFound();

        return Results.Ok(profile.View());
    }

    public static async Task<IResult> List(string? after, int? size, CustomersDb db, CancellationToken token)
    {
        var limit = size ?? 25;
        if (limit is < 1 or > 100)
            return Results.Problem(statusCode: 400, detail: "Page size must be between 1 and 100.");

        var query = db.Profiles.AsNoTracking();
        if (!string.IsNullOrEmpty(after))
            query = query.Where(p => string.Compare(p.Subject, after) > 0);

        var profiles = await query.OrderBy(p => p.Subject).Take(limit + 1).ToListAsync(token);
        var items = profiles.Take(limit).Select(p => p.View()).ToArray();
        return Results.Ok(new { items, next = profiles.Count > limit ? items[^1].Subject : null });
    }

    public static async Task<IResult> UpdateOwn(ClaimsPrincipal user, ProfileInput input, CustomersDb db, CustomerProfileCache cache,
        CancellationToken token)
    {
        var name = input.DisplayName?.Trim();
        if (name is null || name.Length is < 1 or > 100)
            return InvalidName();

        var profile = await db.Profiles.FindAsync([user.FindFirst("sub")!.Value], token);
        if (profile is null)
            return Results.NotFound();
        if (!profile.Active)
            return Results.Problem(statusCode: 403, detail: "The profile is inactive.");
        profile.DisplayName = name;
        return await Save(db, profile, cache, token);
    }

    public static async Task<IResult> SetStatus(string subject, StatusInput input, CustomersDb db,
        CustomerProfileCache cache, CancellationToken token)
    {
        if (input.Active is null || !Enum.TryParse<KycStatus>(input.Kyc, ignoreCase: true, out var status)
            || !string.Equals(input.Kyc, status.ToString(), StringComparison.OrdinalIgnoreCase))
            return Results.Problem(statusCode: 400, detail: "Supply active and one of Pending, Verified, or Rejected.");

        var profile = await db.Profiles.FindAsync([subject], token);
        if (profile is null)
            return Results.NotFound();
        profile.Active = input.Active.Value;
        profile.Kyc = status;
        db.Add(LifecycleOutbox.Create("customer.status-changed", subject));
        return await Save(db, profile, cache, token);
    }

    private static async Task<IResult> Save(CustomersDb db, CustomerProfile profile,
        CustomerProfileCache cache, CancellationToken token)
    {
        try
        {
            await db.SaveChangesAsync(token);
            await cache.RemoveAsync(profile.Subject, token);
            return Results.Ok(profile.View());
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, detail: "The profile changed. Reload and retry.");
        }
    }

    private static IResult InvalidName() =>
        Results.Problem(statusCode: 400, detail: "Display name must contain 1 to 100 characters.");
}

public sealed record ProfileInput(string? DisplayName);
public sealed record StatusInput(string? Kyc, bool? Active);

