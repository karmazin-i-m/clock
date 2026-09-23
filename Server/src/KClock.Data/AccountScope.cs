using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KClock.Data;

/// <summary>
/// The one place the app.account_id GUC is ever set (DESIGN.md §8). Two traps this exists to
/// avoid:
///
/// Trap one: "SET LOCAL app.account_id = $1" is a SQL syntax error — SET does not take
/// parameters. set_config(name, value, is_local) is an ordinary function call and IS
/// parameterisable, so it is what must be used here, never string concatenation.
///
/// Trap two: plain SET (without LOCAL/is_local=true) is session-scoped, and Npgsql pools
/// connections — a GUC set for one request would survive on the pooled connection into a
/// later request for a different account. set_config(..., true) is transaction-scoped and
/// safe, but ONLY inside an explicit transaction, which is why this method requires one to
/// already be open (see AccountScopeFilter in KClock.Api, which is the only caller).
/// </summary>
public static class AccountScope
{
    public static async Task SetCurrentAccountAsync(this DbContext db, Guid accountId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "SetCurrentAccountAsync must run inside an explicit transaction — set_config(..., true) is " +
                "transaction-scoped, and without one it would leak onto a pooled connection for another account.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.account_id', {accountId.ToString()}, true)", ct);
    }
}
