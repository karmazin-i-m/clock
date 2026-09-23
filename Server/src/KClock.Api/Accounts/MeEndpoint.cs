using KClock.Data;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

public static class MeEndpoint
{
    // No predicate needed — ClockDbContext's own query filter (id = current_account_id())
    // already narrows this to exactly the caller's own row.
    public static async Task<IResult> HandleAsync(ClockDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(ct);
        return account is null
            ? Results.NotFound()
            : Results.Ok(new { id = account.Id, email = account.Email, display_name = account.DisplayName });
    }
}
