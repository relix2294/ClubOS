using System.Runtime.InteropServices;
using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.SessionHost;

/// <summary>
/// Низкоуровневый хук клавиатуры (WH_KEYBOARD_LL) только на время, пока показан экран клуба:
/// глушит Win, Alt+Tab, Ctrl+Esc и т.п. (<see cref="ShellKeyFilter"/>). Работает в процессе пользователя,
/// снимается при скрытии экрана и при выходе. Ctrl+Alt+Del не перехватывается.
/// </summary>
internal static unsafe partial class KeyboardGuard
{
    private const int WhKeyboardLl = 13;
    private const int VkControl = 0x11;
    private const int LlkhfAltDown = 0x20;

    private static IntPtr s_hook;

    public static bool Enabled => s_hook != IntPtr.Zero;

    /// <summary>Вызывать из UI-потока (у него есть цикл сообщений, нужный LL-хуку).</summary>
    public static void Enable()
    {
        if (s_hook != IntPtr.Zero)
        {
            return;
        }

        s_hook = SetWindowsHookExW(WhKeyboardLl, &HookProc, GetModuleHandleW(null), 0);
    }

    public static void Disable()
    {
        if (s_hook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(s_hook);
        s_hook = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && lParam != IntPtr.Zero)
        {
            var info = (KbdLlHookStruct*)lParam;
            var alt = (info->Flags & LlkhfAltDown) != 0;
            var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
            if (ShellKeyFilter.ShouldBlock((int)info->VkCode, alt, ctrl))
            {
                return 1;
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, delegate* unmanaged<int, IntPtr, IntPtr, IntPtr> lpfn,
        IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? moduleName);
}
