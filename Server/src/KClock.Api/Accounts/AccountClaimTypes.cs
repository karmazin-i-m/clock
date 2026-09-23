namespace KClock.Api.Accounts;

public static class AccountClaimTypes
{
    /// <summary>
    /// Added to the cookie identity in GoogleAuthEndpoints' OnCreatingTicket hook, after
    /// find_or_create_account resolves it — never the Google subject itself, which is a
    /// separate, provider-scoped identifier (DESIGN.md §7's external_login table).
    /// </summary>
    public const string AccountId = "kclock:account_id";
}
