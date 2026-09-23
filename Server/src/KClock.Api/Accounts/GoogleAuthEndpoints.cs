using System.Security.Claims;
using KClock.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

/// <summary>
/// Google sign-in itself is wired in Program.cs (AddGoogle + OnCreatingTicket, which is where
/// find_or_create_account actually runs) — there is deliberately no MapGet("/auth/google/
/// callback") here. Setting GoogleOptions.CallbackPath to that route is what makes the OAuth
/// handler's own middleware answer it before routing ever sees the request; a hand-written
/// endpoint there would have to reimplement the handshake. This file only maps
/// /auth/google/start (a plain challenge) and /api/auth/signout.
/// </summary>
public static class GoogleAuthEndpoints
{
    public static IResult Start() =>
        Results.Challenge(
            new AuthenticationProperties { RedirectUri = "/" },
            [GoogleDefaults.AuthenticationScheme]);

    public static async Task<IResult> SignOutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    /// <summary>
    /// Runs with no app.account_id GUC set yet — that is precisely the case account's own RLS
    /// policy cannot satisfy for a brand-new row. find_or_create_account is SECURITY DEFINER,
    /// owned by clock_migrator (RLS-exempt as owner), so it works regardless (DESIGN.md §8,
    /// and the IsolationViewsAndGrants migration's doc comment).
    /// </summary>
    public static async Task OnCreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<ClockDbContext>();
        var principal = context.Principal ?? throw new InvalidOperationException("Google did not return a principal.");
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Google did not return a subject claim.");
        var email = principal.FindFirstValue(ClaimTypes.Email);
        var name = principal.FindFirstValue(ClaimTypes.Name);

        var accountId = await FindOrCreateAccountAsync(db, "google", subject, email, name);

        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(AccountClaimTypes.AccountId, accountId.ToString()));
    }

    public static Task<Guid> FindOrCreateAccountAsync(
        ClockDbContext db, string provider, string subject, string? email, string? name, CancellationToken ct = default) =>
        db.Database
            .SqlQueryRaw<Guid>(
                // SqlQueryRaw<scalar> composes as SELECT s."Value" FROM (...) s, so the
                // column must be named Value or the query fails.
                """SELECT find_or_create_account({0}, {1}, {2}, {3}) AS "Value" """,
                provider, subject, (object?)email ?? DBNull.Value, (object?)name ?? DBNull.Value)
            .SingleAsync(ct);
}
