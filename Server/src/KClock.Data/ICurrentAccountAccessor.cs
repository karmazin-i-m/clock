namespace KClock.Data;

/// <summary>
/// Resolves the authenticated account for the current request. Null means unauthenticated —
/// every ClockDbContext query filter treats that as "match nothing", mirroring
/// current_setting('app.account_id', true) returning NULL server-side (DESIGN.md §8).
/// </summary>
public interface ICurrentAccountAccessor
{
    Guid? AccountId { get; }
}
