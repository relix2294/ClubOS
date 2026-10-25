using System.Runtime.Versioning;
using System.Security.Cryptography;
using ClubOS.Agent.Core;

namespace ClubOS.Agent.Service;

/// <summary>
/// DPAPI (CurrentUser-scope учётной записи службы = LocalSystem): приватный ключ устройства
/// расшифровывается только под этой учётной записью на этом ПК. Каталог данных дополнительно
/// закрыт ACL (SYSTEM + Administrators) установочным скриптом.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = "ClubOS.Agent.DeviceKey.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}
