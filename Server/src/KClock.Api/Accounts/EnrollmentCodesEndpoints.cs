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

    public record CreateResponse(string Id, string Code, DateTimeOffset ExpiresAt);

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

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        db.EnrollmentCodes.Add(new EnrollmentCode
        {
            CodeHash = hash,
            AccountId = accountId,
            CreatedAt = now,
            ExpiresAt = now + Ttl,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new CreateResponse(PublicId(hash), $"{code[..4]}-{code[4..]}", now + Ttl));
    }

    /// <summary>
    /// What the add page polls while it waits for the clock: its own code turning "used",
    /// and then the device it went to, which is where the page goes next.
    /// </summary>
    public static async Task<IResult> ListAsync(ClockDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var codes = await db.EnrollmentCodes
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        return Results.Ok(codes.Select(c => new
        {
            id = PublicId(c.CodeHash),
            status = c.ConsumedAt is not null ? "used" : c.ExpiresAt <= now ? "expired" : "active",
            created_at = c.CreatedAt,
            expires_at = c.ExpiresAt,
            consumed_at = c.ConsumedAt,
            consumed_device_id = c.ConsumedDeviceId,
        }));
    }

    /// <summary>
    /// A handle for a code that is not the code: eight bytes of the sha256 the row is keyed
    /// by. The plaintext is never stored, so this is the only thing a page can refer to it by.
    /// </summary>
    private static string PublicId(byte[] codeHash) => Convert.ToHexStringLower(codeHash, 0, 8);

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
