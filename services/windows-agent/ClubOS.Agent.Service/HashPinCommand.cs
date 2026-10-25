using System.Text;
using ClubOS.Agent.Core.PlayerShell;

namespace ClubOS.Agent.Service;

/// <summary>
/// <c>ClubOS.Agent.Service.exe hash-pin</c>: читает PIN техника и печатает хэш для <c>Agent:Shell:TechnicianPinHash</c>.
/// Источник PIN: переменная окружения <see cref="EnvironmentVariable"/> (так передаёт install-agent.ps1 — без
/// конвейера и его кодировок), stdin или скрытый ввод с клавиатуры. Сам PIN нигде не сохраняется.
/// </summary>
internal static class HashPinCommand
{
    public const string EnvironmentVariable = "CLUBOS_TECHNICIAN_PIN";

    public static int Run()
    {
        string? pin;
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            pin = TechnicianPin.NormalizeInput(fromEnvironment);
        }
        else if (Console.IsInputRedirected)
        {
            pin = Console.In.ReadLine() is { } line ? TechnicianPin.NormalizeInput(line) : null;
        }
        else
        {
            Console.Error.Write($"PIN техника ({TechnicianPin.MinLength}–{TechnicianPin.MaxLength} цифр): ");
            pin = ReadHidden();
            Console.Error.Write("Повторите PIN: ");
            if (ReadHidden() != pin)
            {
                Console.Error.WriteLine("PIN не совпадает.");
                return 1;
            }
        }

        if (pin is null)
        {
            Console.Error.WriteLine("PIN не введён.");
            return 1;
        }

        if (TechnicianPin.ValidateFormat(pin) is { } error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        Console.Out.WriteLine(TechnicianPin.Hash(pin));
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}
