using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FinFlow.Ledger.Api;

public static class Accounts
{
    public static async Task<IResult> Open(OpenAccount request, LedgerDb db, CancellationToken token)
    {
        var name = request.Name?.Trim();
        if (name is not { Length: >= 1 and <= 100 } || request.OwnerSubject is not { Length: >= 1 and <= 128 }
            || request.OwnerSubject.Any(char.IsWhiteSpace))
            return Results.Problem(statusCode: 400, detail: "Supply a name and the owner's identity subject.");

        var account = new Account { Id = Guid.NewGuid(), Name = name, OwnerSubject = request.OwnerSubject };
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

