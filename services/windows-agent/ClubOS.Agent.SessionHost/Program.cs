namespace ClubOS.Agent.SessionHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Один экземпляр на пользовательскую сессию.
        using var mutex = new Mutex(true, @"Local\ClubOS.Agent.SessionHost", out var created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new HostContext());
    }
}
