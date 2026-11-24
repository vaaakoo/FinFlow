using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public static class Accounts
{
    public static Task<List<AccountView>> List(LedgerDb db, CancellationToken token) =>
        db.Accounts.AsNoTracking().OrderBy(account => account.Id)
            .Select(account => new AccountView(account.Id, account.Name, account.Balance, "GEL"))
            .ToListAsync(token);

    public static async Task<IResult> Open(OpenAccount request, LedgerDb db, CancellationToken token)
    {
        var name = request.Name?.Trim();
        var ownerSubject = request.OwnerSubject;
        if (string.IsNullOrEmpty(name) || name.Length > 100
            || string.IsNullOrEmpty(ownerSubject) || ownerSubject.Length > 128 || ownerSubject.Any(char.IsWhiteSpace))
            return Results.Problem(statusCode: 400, detail: "Supply a name and the owner's identity subject.");

        var account = new Account { Id = Guid.NewGuid(), Name = name, OwnerSubject = ownerSubject };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(token);
        return Results.Created($"/accounts/{account.Id}/statement",
            new AccountView(account.Id, account.Name, account.Balance, "GEL"));
    }

    public static async Task<List<AccountView>> ListOwned(ClaimsPrincipal user, LedgerDb db, CancellationToken token)
    {
        var subject = user.FindFirst("sub")!.Value;
        return await db.Accounts.AsNoTracking().Where(account => account.OwnerSubject == subject)
            .OrderBy(account => account.Id)
            .Select(account => new AccountView(account.Id, account.Name, account.Balance, "GEL"))
            .ToListAsync(token);
    }
}

public sealed record OpenAccount(string? Name, string? OwnerSubject);
public sealed record AccountView(Guid Id, string Name, long BalanceMinor, string Currency);

