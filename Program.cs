namespace BtAudioGuard;

internal static class Program
{
    /// <summary>App version, e.g. "1.0.0".</summary>
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\BtAudioGuard", out bool firstInstance);
        if (!firstInstance)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
