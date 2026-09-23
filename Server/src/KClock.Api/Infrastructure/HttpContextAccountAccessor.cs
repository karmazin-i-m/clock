using KClock.Api.Accounts;
using KClock.Data;

namespace KClock.Api.Infrastructure;

/// <summary>
/// Feeds ClockDbContext's EF global query filters (the "belt" — see DESIGN.md §8's "Also add
/// EF global query filters"). The "braces" is the Postgres GUC AccountScopeFilter sets
/// separately; the two are independent layers that happen to read the same claim.
/// </summary>
public sealed class HttpContextAccountAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentAccountAccessor
{
    public Guid? AccountId
    {
        get
        {
            var claim = httpContextAccessor.HttpContext?.User.FindFirst(AccountClaimTypes.AccountId);
            return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : null;
        }
    }
}
