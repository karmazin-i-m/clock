using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using KClock.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KClock.Api.Devices;

/// <summary>
/// SHA-256 hash of the bearer token, one indexed query joining credential -> device -> binding
/// filtered on revoked_at IS NULL AND unbound_at IS NULL (DESIGN.md §6). No cache — 100 devices
/// is 1.7 lookups/s, and a cache is precisely what would make revocation take until TTL instead
/// of effect-on-next-request. Not constant-time on purpose: the token is 256 bits of CSPRNG
/// output, so there is no offline search to slow down, and this is a btree probe on a hash, not
/// a password comparison.
/// </summary>
public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IngestDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceToken";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.Fail("Missing Authorization header.");
        }

        var value = header.ToString();
        if (!value.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Authorization header is not a Bearer token.");
        }

        var token = value[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return AuthenticateResult.Fail("Empty bearer token.");
        }

        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        var match = await db.DeviceCredentials
            .Where(c => c.TokenHash == tokenHash && c.RevokedAt == null)
            .Join(
                db.DeviceBindings.Where(b => b.UnboundAt == null),
                c => c.BindingId,
                b => b.Id,
                (c, b) => new { CredentialDeviceId = c.DeviceId, BindingId = b.Id })
            .SingleOrDefaultAsync();

        if (match is null)
        {
            return AuthenticateResult.Fail("Unknown, revoked, or unbound credential.");
        }

        ClaimsIdentity identity = new(
            [
                new Claim(DeviceClaimTypes.DeviceId, match.CredentialDeviceId.ToString()),
                new Claim(DeviceClaimTypes.BindingId, match.BindingId.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
