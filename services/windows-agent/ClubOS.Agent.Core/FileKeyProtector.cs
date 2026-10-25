namespace ClubOS.Agent.Core;

/// <summary>
/// Без шифрования: ключ хранится в файле с правами 600. Только для dev/Linux и симулятора;
/// Windows Agent использует DPAPI (DpapiKeyProtector).
/// </summary>
public sealed class FileKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedData) => protectedData;
}
