using System.Text;
using ClubOS.CloudApi.Auth;
using ClubOS.CloudApi.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>TOTP по эталонным векторам RFC 6238, Base32, шифрование секрета, коды восстановления.</summary>
public class MfaTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]           // RFC 6238, Appendix B (SHA1), последние 6 цифр
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Totp_matches_rfc6238_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));

    [Fact]
    public void Verify_accepts_one_step_drift_and_rejects_replay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 1), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 2), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: step));
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step)[..3] + " " + Totp.Code(RfcSecret, step)[3..], now, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Malformed_codes_are_rejected(string? code) =>
        Assert.Null(Totp.Verify(RfcSecret, code, DateTimeOffset.UtcNow, 0));

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("foobar", "MZXW6YTBOI")] // RFC 4648 §10
    public void Base32_matches_rfc4648(string input, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(input)));
        Assert.Equal(input, Encoding.ASCII.GetString(Base32.Decode(expected.ToLowerInvariant())));
    }

    [Fact]
    public void Otpauth_uri_is_escaped()
    {
        var uri = Totp.OtpAuthUri("ClubOS", "owner+1@club.tj", RfcSecret);
        Assert.StartsWith("otpauth://totp/ClubOS:owner%2B1%40club.tj?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("&period=30", uri);
    }

    private static SecretProtector Protector(string signingKey, string? mfaKey = null)
    {
        var options = Options.Create(new AuthOptions { SigningKey = signingKey, MfaEncryptionKey = mfaKey });
        var keys = new SigningKeyProvider(options, new FakeEnv(), NullLogger<SigningKeyProvider>.Instance);
        return new SecretProtector(options, keys);
    }

    [Fact]
    public void Secret_protector_round_trips_and_detects_tampering()
    {
        var protector = Protector("unit-test-signing-key-0123456789abcdefgh");
        var secret = Totp.GenerateSecret();

        var a = protector.Protect(secret);
        var b = protector.Protect(secret);
        Assert.StartsWith("v1:", a);
        Assert.NotEqual(a, b); // случайный nonce
        Assert.Equal(secret, protector.Unprotect(a));

        var bytes = Convert.FromBase64String(a[3..]);
        bytes[^1] ^= 0x01;
        Assert.Null(protector.Unprotect("v1:" + Convert.ToBase64String(bytes)));
        Assert.Null(protector.Unprotect("garbage"));
        Assert.Null(Protector("another-signing-key-0123456789abcdefghij").Unprotect(a)); // другой ключ
        Assert.Null(Protector("unit-test-signing-key-0123456789abcdefgh", "dedicated-mfa-key-0123456789abcdefghij").Unprotect(a));
    }

    [Fact]
    public void Recovery_codes_are_random_and_normalized_for_hashing()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => MfaService.NewRecoveryCode()).ToList();
        Assert.Equal(200, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}$", c));
        Assert.Equal(MfaService.HashRecovery("abcde-fghjk"), MfaService.HashRecovery(" ABCDE FGHJK "));
        Assert.NotEqual(MfaService.HashRecovery("abcde-fghjk"), MfaService.HashRecovery("abcde-fghjm"));
    }

    [Theory]
    [InlineData("Owner,Admin", "Owner", true)]
    [InlineData("Owner, Admin", "Admin", true)]
    [InlineData("Owner,Admin", "Operator", false)]
    [InlineData("", "Owner", false)]
    [InlineData("owner", "Owner", false)] // роли чувствительны к регистру, как в БД
    public void Required_roles_parsing(string config, string role, bool required) =>
        Assert.Equal(required, new AuthOptions { MfaRequiredRoles = config }.IsMfaRequired(role));

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
