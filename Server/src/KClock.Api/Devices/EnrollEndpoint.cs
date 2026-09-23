using System.Security.Cryptography;
using System.Text;
using KClock.Data;
using KClock.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KClock.Api.Devices;

/// <summary>
/// POST /d/v1/enroll — DESIGN.md §5.1. Exchange is one transaction, and it is also the transfer
/// mechanism: the seven steps below run in order, in a single explicit transaction, and must
/// stay in order (closing the old binding and revoking old credentials before the new binding
/// exists is what makes a re-enrollment coherent rather than racy).
/// </summary>
public static class EnrollEndpoint
{
    public static async Task<DeviceResult> HandleAsync(
        EnrollRequest request, IngestDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Hw) || string.IsNullOrWhiteSpace(request.Mac))
        {
            return DeviceResult.BadRequest();
        }

        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(request.Code)));

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var code = await db.EnrollmentCodes.SingleOrDefaultAsync(c => c.CodeHash == codeHash, ct);
        if (code is null)
        {
            // No stored code hashes to this value at all — nothing to burn; the 32^8 Crockford
            // base32 keyspace (DESIGN.md §5.1) makes guessing impractical regardless of any
            // per-code attempt counter.
            return DeviceResult.Error(400, "code_invalid");
        }

        var now = clock.GetUtcNow();

        if (code.ConsumedAt is not null)
        {
            return DeviceResult.Error(409, "code_used");
        }

        if (code.Attempts >= 5)
        {
            return DeviceResult.Error(400, "code_invalid");
        }

        if (code.ExpiresAt <= now)
        {
            code.Attempts++;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return DeviceResult.Error(400, "code_expired");
        }

        // Step 1: find or create device by hardware_id = mac — never duplicate, this is what
        // makes transfer coherent.
        var normalizedMac = request.Mac.Trim().ToUpperInvariant();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.HardwareId == normalizedMac, ct);
        if (device is null)
        {
            device = new Device
            {
                HardwareId = normalizedMac,
                Profile = request.Hw,
                Model = request.Model,
                CreatedAt = now,
            };
            db.Devices.Add(device);
            await db.SaveChangesAsync(ct);
        }

        // Step 2: close any open binding (unbound_at = now()).
        var openBinding = await db.DeviceBindings.SingleOrDefaultAsync(b => b.DeviceId == device.Id && b.UnboundAt == null, ct);
        if (openBinding is not null)
        {
            openBinding.UnboundAt = now;
            openBinding.UnboundReason = "re-enrolled";
        }

        // Step 3: revoke all credentials for the device.
        var activeCredentials = await db.DeviceCredentials
            .Where(c => c.DeviceId == device.Id && c.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var credential in activeCredentials)
        {
            credential.RevokedAt = now;
            credential.RevokedReason = "re-enrolled";
        }

        await db.SaveChangesAsync(ct);

        // Step 4: insert the new binding for the code's account.
        var binding = new DeviceBinding { DeviceId = device.Id, AccountId = code.AccountId, BoundAt = now };
        db.DeviceBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        // Step 5: insert device_access(role='owner').
        db.DeviceAccesses.Add(new DeviceAccess { BindingId = binding.Id, AccountId = code.AccountId, Role = DeviceAccessRole.Owner });

        // Step 6: 32 CSPRNG bytes -> token; store sha256(token). The plaintext token is never
        // stored — only its hash — and never logged.
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = "kcd1_" + Base64UrlEncode(tokenBytes);
        db.DeviceCredentials.Add(new DeviceCredential
        {
            DeviceId = device.Id,
            BindingId = binding.Id,
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            IssuedAt = now,
        });

        // Step 7: mark the code consumed.
        code.ConsumedAt = now;
        code.ConsumedDeviceId = device.Id;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return DeviceResult.Ok(new EnrollResponse(device.Id, token, PolicyResolver.Resolve(device), now.ToUnixTimeSeconds()));
    }

    private static string NormalizeCode(string code) =>
        code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
