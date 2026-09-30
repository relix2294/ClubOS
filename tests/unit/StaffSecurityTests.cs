using System.Security.Cryptography;
using ClubOS.CloudApi.Domain;
using ClubOS.CloudApi.Security;
using Xunit;

namespace ClubOS.Unit.Tests;

/// <summary>Пароли (Argon2id + миграция с PBKDF2), политика паролей и матрица прав ролей (ТЗ §8, §27.2).</summary>
public class StaffSecurityTests
{
    [Fact]
    public void Argon2id_hash_verifies_and_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery");
        Assert.StartsWith("argon2id$v=19$m=19456,t=2,p=1$", hash);
        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("correct horse batterY", hash));
        Assert.False(PasswordHasher.NeedsRehash(hash));
    }

    [Fact]
    public void Same_password_gets_different_salt()
    {
        Assert.NotEqual(PasswordHasher.Hash("same-password-1"), PasswordHasher.Hash("same-password-1"));
    }

    [Fact]
    public void Legacy_pbkdf2_hash_from_M0_still_verifies_and_needs_rehash()
    {
        // Формат M0: {iterations}.{saltB64}.{hashB64}, PBKDF2-HMAC-SHA256.
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2("legacy-password", salt, 210_000, HashAlgorithmName.SHA256, 32);
        var legacy = $"210000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";

        Assert.True(PasswordHasher.Verify("legacy-password", legacy));
        Assert.False(PasswordHasher.Verify("other-password", legacy));
        Assert.True(PasswordHasher.NeedsRehash(legacy));
    }

    [Theory]
    [InlineData("argon2id$v=19$m=999999999,t=2,p=1$AAAA$AAAA")] // «ядовитая» память
    [InlineData("argon2id$v=19$m=19456,t=99,p=1$AAAA$AAAA")]    // слишком много итераций
    [InlineData("argon2id$garbage")]
    [InlineData("1.not-base64.x")]
    [InlineData("")]
    public void Malformed_or_dangerous_hashes_are_rejected(string encoded)
    {
        Assert.False(PasswordHasher.Verify("anything-123", encoded));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("aaaaaaaaaaaa", false)]            // мало разных символов
    [InlineData("ivan.petrov-2026!", false)]       // содержит часть email
    [InlineData("Club-Admin-2026!", true)]
    public void Password_policy(string password, bool ok)
    {
        Assert.Equal(ok, PasswordPolicy.Validate(password, "ivan.petrov@club.tj") is null);
    }

    [Fact]
    public void Temporary_password_is_random_and_meets_policy()
    {
        var a = PasswordPolicy.GenerateTemporary();
        var b = PasswordPolicy.GenerateTemporary();
        Assert.NotEqual(a, b);
        Assert.Null(PasswordPolicy.Validate(a, "someone@club.tj"));
    }

    [Theory]
    [InlineData(Roles.Owner, Permissions.StaffManage, true)]
    [InlineData(Roles.Admin, Permissions.StaffManage, false)]
    [InlineData(Roles.Admin, Permissions.EnrollmentManage, true)]
    [InlineData(Roles.Operator, Permissions.EnrollmentManage, false)]
    [InlineData(Roles.Operator, Permissions.DevicesCommand, true)]
    [InlineData(Roles.Operator, Permissions.SessionsManage, true)]
    [InlineData(Roles.Operator, Permissions.CashOperate, true)]
    [InlineData(Roles.Operator, Permissions.CashRefund, false)]
    [InlineData(Roles.Operator, Permissions.ReportsView, false)]
    [InlineData(Roles.Admin, Permissions.CashRefund, true)]
    [InlineData(Roles.Admin, Permissions.ReportsView, true)]
    [InlineData("Hacker", Permissions.DevicesView, false)]
    public void Role_permission_matrix(string role, string permission, bool allowed)
    {
        Assert.Equal(allowed, Permissions.Has(role, permission));
    }
}
