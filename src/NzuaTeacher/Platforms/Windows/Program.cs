using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NzuaTeacher.WinUI;

/// <summary>
/// Власна точка входу: nzua-mcp встановлює Chromium, запускаючи цей же exe з --install-chromium,
/// тож аргумент треба обробити до ініціалізації вікна.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--install-chromium")
            return Microsoft.Playwright.Program.Main(["install", "chromium"]);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
