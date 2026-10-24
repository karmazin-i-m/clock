using KClock.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
        // A session minted before the account was deleted elsewhere still decrypts, but the
        // account behind it is a tombstone: answer as if signed out.
        if (account?.DeletedAt is not null)
        {
            return Results.Unauthorized();
        }

        return account is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                id = account.Id,
                email = account.Email,
                display_name = account.DisplayName,
                picture = http.User.FindFirst(AccountClaimTypes.Picture)?.Value,
                created_at = account.CreatedAt,
            });
    }

    /// <summary>
    /// DESIGN.md §10, phase 1, inside the request: delete_account() closes and revokes
    /// everything, queues the telemetry for the purge job, and severs the Google identity, all
    /// in AccountScopeFilter's transaction. Then the session goes too — the account it names no
    /// longer exists for any purpose.
    /// </summary>
    public static async Task<IResult> DeleteAsync(ClockDbContext db, HttpContext http, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SELECT delete_account()", ct);
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }
}
