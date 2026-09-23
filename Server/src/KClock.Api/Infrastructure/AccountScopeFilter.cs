using KClock.Api.Accounts;
using KClock.Data;

namespace KClock.Api.Infrastructure;

/// <summary>
/// An explicit transaction per authenticated request, opened here rather than left to
/// SaveChangesAsync's implicit one, because most of the browser API is reads and
/// ToListAsync does not open a transaction on its own (DESIGN.md §8, trap two). Mapped onto
/// app.MapGroup("/api") only — /health/* and /auth/* are outside this group by construction,
/// not by an exception list.
/// </summary>
public sealed class AccountScopeFilter(ClockDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var ct = context.HttpContext.RequestAborted;
        var claim = context.HttpContext.User.FindFirst(AccountClaimTypes.AccountId);
        if (claim is null || !Guid.TryParse(claim.Value, out var accountId))
        {
            return Results.Unauthorized();
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SetCurrentAccountAsync(accountId, ct);

        try
        {
            var result = await next(context);
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
