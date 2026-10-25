using System.Security.Cryptography;
using ClubOS.CloudApi.Data;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Infrastructure;
using ClubOS.CloudApi.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClubOS.CloudApi.Auth;

public enum MfaCheck
{
    Ok,
    Invalid,
    Expired,
    TooManyAttempts
}

/// <summary>
/// MFA сотрудников (TOTP, ТЗ §8): настройка, второй шаг входа, коды восстановления, сброс.
/// Все проверки кода — в одном месте: защита от повтора кода, лимит попыток на challenge.
/// </summary>
public sealed class MfaService(ClubOsDbContext db, SecretProtector protector, IOptions<AuthOptions> options, TimeProvider time)
{
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    public const int MaxChallengeAttempts = 5;
    public const int RecoveryCodeCount = 10;

    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789"; // без 0/o, 1/l/i

    // ---------- Настройка ----------

    public (string Secret, string Uri) BeginSetup(User user)
    {
        var secret = Totp.GenerateSecret();
        user.MfaPendingSecretProtected = protector.Protect(secret);
        user.MfaPendingCreatedAtUtc = time.GetUtcNow();
        return (Base32.Encode(secret), Totp.OtpAuthUri(options.Value.MfaIssuer, user.Email, secret));
    }

    /// <summary>Подтверждение первым кодом. Возвращает коды восстановления (показываются один раз) или null.</summary>
    public async Task<IReadOnlyList<string>?> CompleteSetupAsync(User user, string? code, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (user.MfaPendingCreatedAtUtc is not { } created || now - created > PendingLifetime ||
            protector.Unprotect(user.MfaPendingSecretProtected) is not { } secret)
        {
            return null;
        }

        if (Totp.Verify(secret, code, now, lastUsedStep: 0) is not { } step)
        {
            return null;
        }

        user.MfaEnabled = true;
        user.MfaSecretProtected = user.MfaPendingSecretProtected;
        user.MfaPendingSecretProtected = null;
        user.MfaPendingCreatedAtUtc = null;
        user.MfaLastUsedStep = step;
        user.MfaEnabledAtUtc = now;
        return await ReplaceRecoveryCodesAsync(user, ct);
    }

    public async Task DisableAsync(User user, CancellationToken ct)
    {
        user.MfaEnabled = false;
        user.MfaSecretProtected = null;
        user.MfaPendingSecretProtected = null;
        user.MfaPendingCreatedAtUtc = null;
        user.MfaLastUsedStep = 0;
        user.MfaEnabledAtUtc = null;
        await db.MfaRecoveryCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(User user, CancellationToken ct)
    {
        await db.MfaRecoveryCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        foreach (var code in codes)
        {
            db.MfaRecoveryCodes.Add(new MfaRecoveryCode
            {
                Id = Ids.New("mrc"),
                UserId = user.Id,
                CodeHash = HashRecovery(code),
                CreatedAtUtc = now
            });
        }

        return codes;
    }

    public Task<int> RecoveryCodesLeftAsync(string userId, CancellationToken ct) =>
        db.MfaRecoveryCodes.CountAsync(x => x.UserId == userId && x.UsedAtUtc == null, ct);

    // ---------- Проверка кода ----------

    /// <summary>TOTP (с защитой от повтора) или неиспользованный код восстановления.</summary>
    public async Task<bool> VerifyAsync(User user, string? code, string? recoveryCode, CancellationToken ct)
    {
        if (!user.MfaEnabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            if (protector.Unprotect(user.MfaSecretProtected) is not { } secret ||
                Totp.Verify(secret, code, time.GetUtcNow(), user.MfaLastUsedStep) is not { } step)
            {
                return false;
            }

            user.MfaLastUsedStep = step;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var hash = HashRecovery(recoveryCode);
            var stored = await db.MfaRecoveryCodes.SingleOrDefaultAsync(
                x => x.UserId == user.Id && x.CodeHash == hash && x.UsedAtUtc == null, ct);
            if (stored is null)
            {
                return false;
            }

            stored.UsedAtUtc = time.GetUtcNow();
            return true;
        }

        return false;
    }

    // ---------- Второй шаг входа ----------

    public (MfaChallenge Challenge, string Token) CreateChallenge(User user)
    {
        var token = Ids.NewSecret();
        var now = time.GetUtcNow();
        var challenge = new MfaChallenge
        {
            Id = Ids.New("mfc"),
            UserId = user.Id,
            TokenHash = Ids.HashSecret(token),
            CreatedAtUtc = now,
            ExpiresAtUtc = now + ChallengeLifetime
        };
        db.MfaChallenges.Add(challenge);
        return (challenge, token);
    }

    /// <summary>Проверяет код по challenge. При успехе challenge погашен и возвращается пользователь.</summary>
    public async Task<(MfaCheck Result, User? User)> CompleteChallengeAsync(string? token, string? code, string? recoveryCode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (MfaCheck.Expired, null);
        }

        var hash = Ids.HashSecret(token);
        var challenge = await db.MfaChallenges.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (challenge is null || challenge.ConsumedAtUtc is not null || challenge.ExpiresAtUtc <= now)
        {
            return (MfaCheck.Expired, null);
        }

        if (challenge.Attempts >= MaxChallengeAttempts)
        {
            challenge.ConsumedAtUtc = now;
            return (MfaCheck.TooManyAttempts, null);
        }

        var user = await db.Users.SingleAsync(x => x.Id == challenge.UserId, ct);
        if (!user.IsActive)
        {
            challenge.ConsumedAtUtc = now;
            return (MfaCheck.Expired, null);
        }

        if (!await VerifyAsync(user, code, recoveryCode, ct))
        {
            challenge.Attempts++;
            if (challenge.Attempts >= MaxChallengeAttempts)
            {
                challenge.ConsumedAtUtc = now;
                return (MfaCheck.TooManyAttempts, user);
            }

            return (MfaCheck.Invalid, user);
        }

        challenge.ConsumedAtUtc = now;
        return (MfaCheck.Ok, user);
    }

    /// <summary>Удаляет просроченные challenge (вызывается при входе — таблица не растёт).</summary>
    public Task<int> PurgeExpiredChallengesAsync(CancellationToken ct)
    {
        var threshold = time.GetUtcNow() - TimeSpan.FromHours(1);
        return db.MfaChallenges.Where(x => x.ExpiresAtUtc < threshold).ExecuteDeleteAsync(ct);
    }

    // ---------- Коды восстановления ----------

    public static string NewRecoveryCode()
    {
        var raw = RandomNumberGenerator.GetString(RecoveryAlphabet, 10);
        return $"{raw[..5]}-{raw[5..]}";
    }

    public static string HashRecovery(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Ids.HashSecret("mrc:" + normalized);
    }
}
