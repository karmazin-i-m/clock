using System.Security.Cryptography;
using System.Text;
using KClock.Data;
using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Accounts;

/// <summary>
/// Same Crockford base32 alphabet as DESIGN.md §5.1 (no I, L, O, U), rendered XXXX-XXXX,
/// stored only as sha256(code) — the plaintext is returned to the caller exactly once, in the
/// create response, and never persisted.
/// </summary>
public static class EnrollmentCodesEndpoints
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    public record CreateResponse(string Code, DateTimeOffset ExpiresAt);

    public static async Task<IResult> CreateAsync(
        ClockDbContext db, ICurrentAccountAccessor accountAccessor, TimeProvider clock, CancellationToken ct)
    {
        var accountId = accountAccessor.AccountId!.Value;

        var raw = new char[8];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        var code = new string(raw);
        var now = clock.GetUtcNow();

        db.EnrollmentCodes.Add(new EnrollmentCode
        {
            CodeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code)),
            AccountId = accountId,
            CreatedAt = now,
            ExpiresAt = now + Ttl,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new CreateResponse($"{code[..4]}-{code[4..]}", now + Ttl));
    }

    public static async Task<IResult> ListAsync(ClockDbContext db, CancellationToken ct)
    {
        var codes = await db.EnrollmentCodes
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new { created_at = c.CreatedAt, expires_at = c.ExpiresAt, consumed_at = c.ConsumedAt })
            .ToListAsync(ct);
        return Results.Ok(codes);
    }

    // Clears this account's own outstanding (unconsumed) codes — no {id} in DESIGN.md §12's
    // route list, and a code is never individually addressable once issued (it is presented
    // to the device, not looked up).
    public static async Task<IResult> DeleteUnconsumedAsync(ClockDbContext db, CancellationToken ct)
    {
        var unconsumed = await db.EnrollmentCodes.Where(c => c.ConsumedAt == null).ToListAsync(ct);
        db.EnrollmentCodes.RemoveRange(unconsumed);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
