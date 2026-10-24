using KClock.Data;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

public static class MeEndpoint
{
    // No predicate needed — ClockDbContext's own query filter (id = current_account_id())
    // already narrows this to exactly the caller's own row.
    //
    // The avatar is read off the session rather than stored: it is Google's URL, not ours, it
    // changes when the person changes it there, and a session older than the claim simply
    // has none.
    public static async Task<IResult> HandleAsync(ClockDbContext db, HttpContext http, CancellationToken ct)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(ct);
        return account is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                id = account.Id,
                email = account.Email,
                display_name = account.DisplayName,
                picture = http.User.FindFirst(AccountClaimTypes.Picture)?.Value,
            });
    }
}
