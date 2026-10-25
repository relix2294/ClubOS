namespace ClubOS.Agent.Core.PlayerShell;

/// <summary>
/// Какие сочетания клавиш глушит экран клуба, пока ПК закрыт. Ctrl+Alt+Del перехватить нельзя и не нужно:
/// это защищённая последовательность Windows, путь к выходу из системы и Диспетчеру задач остаётся всегда
/// (ТЗ §3.3: не «запирать» ПК). Ctrl+Shift+F12 не глушится — это вход техника.
/// </summary>
public static class ShellKeyFilter
{
    public const int VkTab = 0x09;
    public const int VkEscape = 0x1B;
    public const int VkF4 = 0x73;
    public const int VkF12 = 0x7B;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkApps = 0x5D;

    public static bool ShouldBlock(int virtualKey, bool alt, bool ctrl) => virtualKey switch
    {
        VkLWin or VkRWin => true,                  // меню «Пуск», Win+D, Win+Tab, Win+R…
        VkApps => true,                            // контекстное меню
        VkTab when alt => true,                    // Alt+Tab
        VkEscape when alt || ctrl => true,         // Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc (Диспетчер задач)
        VkF4 when alt => true,                     // Alt+F4
        _ => false
    };
}
